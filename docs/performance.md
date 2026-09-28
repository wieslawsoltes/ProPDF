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
notification can arrive later. Tests that inspect progressive publications
use an explicit synchronous dispatcher; tests of queued hosts explicitly drain
their own queue. These completion points must not be conflated or replaced with
fixed sleeps. Both modes retain the same production coalescing and tile-ownership
behavior.

## Substitute glyph reuse

The optional renderer caches substitute outlines in font-design coordinates, independently of font size, page zoom and tile scale. Each resolved typeface retains at most 256 Unicode/direction mappings and approximately 512 KiB of outlines. Oversized entries remain transient. A held outline lease remains valid after eviction or cache disposal. The glyph cache does not account for the entire font manager, shaper, parser or native allocation footprint; active leases can temporarily exceed its retained-cache budget. See [Text rendering and typography](text-rendering.md) for width fitting and qualification limits.


## Display density and fractional tile edges

The viewer separates logical DIPs from physical raster pixels. A display-density-only change invalidates the tile plan without changing document revision, logical zoom, scrolling or cached PDF display lists. Avalonia listens for top-level scaling changes, WPF handles DPI changes, and Uno listens for XamlRoot changes in addition to size changes. Native per-monitor hardware/driver behavior still needs physical-device testing.

The new `CreateThumbnailAsync(page, width, height, pixelsPerDip, token)` overload draws density-aware previews while preserving DIP dimensions. The original overload retains its 1x behavior. High-density previews use bounded 512-pixel tiles rather than an unlimited full-page allocation. Maximum preview dimensions remain 512 DIPs, density is 0.5–8, and failed/cancelled generation releases all acquired tiles. Adapter thumbnails reload when density changes. At 2x density the same logical preview has approximately four times as many raster pixels, not a stretched 1x image. This costs corresponding raster/cache memory; it is not free supersampling.

Page-edge tiles can have fractional pixel extents but require integer image allocation. Composition now uses the exact source extent instead of squeezing the rounded padding pixel into the logical page. The regression checks a known stripe's position rather than relying on a nonempty image. Linear sampling and the existing gutters remain; this is not a guarantee for every transparency group, unbounded filter or GPU compositor.

## Exact paint-cache identity

The attributed Skia interpreter previously used a 32-bit hash as the entire paint key. Distinct colors or dash sequences with the same hash could reuse the wrong native paint. Keys now compare the full color, alpha, stroke parameters, dash values and blend override. Dash arrays are snapshotted only on misses, so mutating caller-owned input cannot change a stored key. Repeated equal values reuse the existing native paint. Tests deliberately construct color/dash hash collisions and check output colors; this is a correctness repair, not a new ICC/color-management implementation.

## Browser canvas density synchronization

Uno 6.7.135 sizes its browser canvas on `window.resize`, while managed display information can detect a density-only change without that event. The sample's owned `display-density.mjs` observes a resolution media query and rearms it after each actual density change. It coalesces one ordinary resize notification on the next animation frame, allowing Uno's existing host path to resize both the native surface and the managed viewport. It does not poll, patch private runtime methods, change CSS dimensions, reset zoom, or reload the PDF. Ordinary resize events at unchanged density add no notification. The observer disposes on normal page exit and rechecks restored back/forward-cache pages.

Browser tests change only device density, then require unchanged document/zoom/layout, scaled page/thumbnail pixel counts and the same independently observed PDF panel geometry. Captures use CDP directly because Playwright's screenshot helper can restore the original emulation settings. The bounded pixel gate checks header placement, logical bounds and area instead of accepting any nonempty frame. Desktop physical monitor transitions, Safari/Firefox and GPU-driver behavior still require separate qualification.

Upstream host behavior: [Uno 6.7.135 BrowserRenderer](https://github.com/unoplatform/uno/blob/6.7.135/src/Uno.UI.Runtime.Skia.WebAssembly.Browser/ts/Runtime/BrowserRenderer.ts).
