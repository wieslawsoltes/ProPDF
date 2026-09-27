# Rendering responsiveness

ProPDF rasterizes PDF tiles with CPU Skia and presents retained images through each UI framework. This page describes tested scheduling and cache behavior, not a measured speed advantage over Acrobat or a fully GPU PDF interpreter.

## Warm tiles do not wait for parsing

The immutable, reference-counted tile cache has its own short lock. A cached tile can be leased while the renderer's interpreter worker is opening or recording another page. Cache misses still pass through the single asynchronous worker: PDF document, font and display-list state is not accessed concurrently. A second cache check after waiting prevents identical misses from producing duplicate raster work.

On a single-threaded WebAssembly runtime, CPU interpretation can still occupy the browser thread. Cache/scheduling improvements do not provide preemption or a separate worker, and do not establish a browser frame-rate guarantee.

The LRU, byte budget, hit/miss counters, eviction and lease acquisition share the cache lock. Leases retain the image before leaving that lock, so cache eviction or renderer disposal cannot invalidate an image already acquired by a viewport or export. A zero-byte budget still permits transient tiles while retaining no cache entries.

## Scroll composition versus raster work

The viewport computes a bounded list of device-space tile requests. When scrolling changes only the tile positions on screen, but not the document revision, tile coverage or scale, it reuses the current completed **or in-flight** plan. This avoids canceling useful work or submitting fresh cache requests on every wheel event. Zoom, revision changes and tile-boundary crossings still schedule the required work. A failed plan can be retried.

Progressive publication retains previously visible tiles that match the new plan's revision and scale until replacement requests are ready. Unrelated, stale-revision and obsolete-scale tiles are not copied into the new result. Dispatcher notifications are coalesced while queued; consumers read the latest state when the callback executes. No fixed refresh rate or polling timer is introduced.

Tile planning retains the 512-pixel tile edge and the 256-tile viewport limit. This is not total native-memory accounting: active leases, display lists, decoded images, fonts and the document model consume additional memory. Reducing redundant work does not remove the in-process hostile-input boundary.

## Regression evidence

`RendererConcurrencyTests` proves that a cached request completes while a different page's recorder is deliberately blocked. It also covers simultaneous identical misses, canceled cache hits, zero/tiny cache budgets, LRU order and retained image lifetime after disposal.

`ViewportSchedulingTests` checks that 40 scroll updates within the same tile coverage add **zero** cache hits or misses, that such scrolling does not cancel an in-flight recording, and that progressive publication does not drop existing matching tiles. A queued dispatcher receives one notification for 100 immediate state updates and reads the final value. Zoom, revision replacement and viewport-budget failure/recovery are also tested.

These are deterministic work-count and ownership assertions, not hardware timing claims. Run the tests on the target runtime and profile representative documents for throughput, memory and input latency. The browser CI additionally exercises actual Uno/Skia rendering and editing; desktop headless smoke tests cover Avalonia and WPF bindings and pixels.

## Rendering completion and UI publication

`PdfViewportController.WaitForRenderingAsync` waits for the latest tile plan, not
for the host dispatcher or a presented compositor frame. A queued `Invalidated`
notification can arrive later. Tests that inspect every progressive publication
use an explicit synchronous dispatcher; tests of queued hosts explicitly drain
their own queue. These completion points must not be conflated or replaced with
fixed sleeps. Both modes retain the same production coalescing and tile-ownership
behavior.
