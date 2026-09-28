import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';

// A completed PDF transaction and tile task do not guarantee that Uno's queued
// layout/compositor has presented that document. Inspect the actual composited
// screenshot, without calling Refresh, injecting ItemsSource or forcing a PDF edit.
export async function waitForReopenedDocument(page, path, timeout = 15000) {
  const started = Date.now();
  let attempts = 0, navyPixels = 0;
  do {
    const image = await page.screenshot({ timeout: 5000 });
    navyPixels = await page.evaluate(async base64 => {
      const image = new Image(); image.src = 'data:image/png;base64,' + base64; await image.decode();
      const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
      const context = canvas.getContext('2d'); context.drawImage(image, 0, 0);
      const pixels = context.getImageData(0, 0, image.width, image.height).data;
      let count = 0;
      // At the narrow test size both side panes are hidden. Exclude the app header,
      // toolbars and status; the welcome PDF's navy panel must replace the black-M fixture.
      for (let y = 180; y < image.height - 50; y++) for (let x = 20; x < image.width - 20; x++) {
        const i = (y * image.width + x) * 4;
        if (Math.abs(pixels[i] - 24) < 4 && Math.abs(pixels[i + 1] - 43) < 4 && Math.abs(pixels[i + 2] - 79) < 4) count++;
      }
      return count;
    }, image.toString('base64'));
    attempts++;
    if (navyPixels >= 10000) {
      await writeFile(path, image);
      return { attempts, navyPixels, elapsedMilliseconds: Date.now() - started };
    }
    // Polling does not establish success; only pixels belonging to the reopened
    // document do. The bound prevents a stale frame being accepted indefinitely.
    await page.waitForTimeout(100);
  } while (Date.now() - started < timeout);
  assert.fail(`Reopened PDF was not presented within ${timeout} ms (${navyPixels} matching pixels, ${attempts} frames).`);
}
