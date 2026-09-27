import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';
const base = process.env.PROPDF_URL || 'http://127.0.0.1:4173/ProPDF/';
const out = process.env.PROPDF_QA || 'artifacts/browser';
await mkdir(out, { recursive: true });
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] });
const context = await browser.newContext({ viewport: { width: 1600, height: 1050 }, acceptDownloads: true });
const page = await context.newPage();
page.setDefaultTimeout(20000);
const errors = [], requests = [], checks = [], consoleMessages = [];
let checkpoint = 'launch', lastState = null;
function bounded(promise, label, milliseconds = 20000) {
  let timer;
  return Promise.race([promise, new Promise((_, reject) => {
    timer = setTimeout(() => reject(new Error(`Timed out: ${label}`)), milliseconds);
  })]).finally(() => clearTimeout(timer));
}
async function progress(label) {
  checkpoint = label;
  console.log('Browser check:', label);
  await writeFile(`${out}/progress.json`, JSON.stringify({ checkpoint, checks, lastState, errors, requests, consoleMessages }, null, 2));
}
page.on('pageerror', e => { errors.push(e.message); console.error('Unhandled browser error:', e.message); });
page.on('console', e => {
  if (consoleMessages.length < 500) consoleMessages.push({ type: e.type(), text: e.text().slice(0, 3000) });
  if (e.type() === 'error') console.error('Browser:', e.text());
});
page.on('response', r => { if (r.status() >= 400 && /\.(wasm|js|dll|json|ttf|woff2?)(\?|$)/i.test(r.url())) requests.push(`${r.status()} ${r.url()}`); });
page.on('dialog', async dialog => dialog.type() === 'beforeunload' ? dialog.accept() : dialog.dismiss());
async function state() {
  lastState = await bounded(page.evaluate(() => JSON.parse(propdfTest.State())), 'state', 8000);
  return lastState;
}
async function objectsForSelection(label) {
  const objects = JSON.parse(await bounded(page.evaluate(() => propdfTest.Objects()), label));
  // PDF inspection and dispatcher bindings have different completion points.
  // Wait for exact object identity in the real native list, without bypassing it.
  await page.waitForFunction(() => propdfTest.ContentListReady(), null, { timeout: 15000 });
  return objects;
}
async function command(name) {
  await progress(name);
  await bounded(page.evaluate(name => propdfTest.Click(name), name), name);
  await healthy();
}
async function text(name, value) {
  await bounded(page.evaluate(({ name, value }) => propdfTest.Text(name, value), { name, value }), name);
}
async function healthy() {
  const current = await state();
  assert.equal(current.error, null); assert.equal(current.busy, false); return current;
}
async function section(index) {
  await progress(`inspector ${index}`);
  await bounded(page.evaluate(index => propdfTest.Choose('InspectorSection', index), index), 'inspector');
  await page.waitForTimeout(120);
}
async function download(name, filename) {
  const pending = page.waitForEvent('download');
  // Attach a rejection handler immediately so a command failure never leaves an unhandled download waiter.
  const result = pending.then(file => ({ file }), error => ({ error }));
  await command(name);
  const { file, error } = await result; if (error) throw error;
  const path = `${out}/${filename || file.suggestedFilename()}`;
  await file.saveAs(path); assert.equal(await file.failure(), null); return path;
}
const watchdog = setTimeout(async () => {
  await writeFile(`${out}/watchdog.json`, JSON.stringify({ checkpoint, checks, lastState, errors, requests, consoleMessages }, null, 2)).catch(() => {});
  console.error('Browser workflow deadline exceeded at', checkpoint);
  process.exit(1);
}, 7 * 60 * 1000);
try {
  await progress('navigate and initialize actual Uno application');
  await page.goto(base + '?test=1', { waitUntil: 'domcontentloaded' });
  await page.waitForFunction(() => globalThis.propdfTest || document.documentElement.dataset.propdfError, null, { timeout: 240000 });
  const failure = await bounded(page.evaluate(() => document.documentElement.dataset.propdfError), 'startup status');
  assert.equal(failure, undefined);
  await progress('load real PDF tiles and form inspector');
  await page.waitForFunction(() => { const s = JSON.parse(propdfTest.State()); return s.tiles > 0 && s.fields > 0 && s.pageItems === s.pages; }, null, { timeout: 30000 });
  const initial = await healthy(); assert.equal(initial.pages, 3); assert.equal(initial.dirty, false);
  assert.ok(await page.locator('canvas').count() > 0); checks.push('real Uno/Skia application and PDF tiles');
  const original = await bounded(page.evaluate(() => propdfTest.TextContent()), 'independent text extraction');
  assert.ok(original.includes('Your documents.')); checks.push('independent PDF text extraction');
  // Readiness can precede the first compositor frame. Wait for the real splash
  // to leave, then allow layout and composition to present the initialized app.
  await page.locator('.uno-loader').waitFor({ state: 'hidden', timeout: 15000 });
  await bounded(page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))), 'initial presentation');
  const initialPixels = await page.screenshot({ path: `${out}/uno-desktop.png`, timeout: 10000 });
  const painted = await bounded(page.evaluate(async base64 => {
    const image = new Image(); image.src = 'data:image/png;base64,' + base64; await image.decode();
    const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
    const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0);
    const data = ctx.getImageData(200, 180, 800, 600).data; let count = 0;
    for (let i = 0; i < data.length; i += 4)
      if (data[i] < 80 && data[i + 2] > data[i] + 25 && data[i + 2] > data[i + 1] + 15) count++;
    return count;
  }, initialPixels.toString('base64')), 'presented PDF pixels');
  assert.ok(painted > 1000, 'The real browser screenshot must show PDF content, not a blank canvas or splash.');
  checks.push('composited editor screenshot contains rendered PDF pixels');
  await progress('rendered page thumbnails and pointer navigation');
  await page.waitForFunction(() => JSON.parse(propdfTest.Thumbnails()).some(t => t.page === 2), null, { timeout: 15000 });
  const thumbnails = JSON.parse(await bounded(page.evaluate(() => propdfTest.Thumbnails()), 'thumbnail geometry'));
  const firstThumbnail = thumbnails.find(t => t.page === 1), secondThumbnail = thumbnails.find(t => t.page === 2);
  assert.ok(firstThumbnail && secondThumbnail);
  // Read pixels from the actual composited thumbnail region. A list of blank
  // placeholders does not qualify as rendered thumbnails.
  let thumbnailPixels = 0;
  for (let attempt = 0; attempt < 10 && thumbnailPixels < 100; attempt++) {
    const pixels = await page.screenshot({ timeout: 10000 });
    thumbnailPixels = await bounded(page.evaluate(async ({ base64, bounds }) => {
      const image = new Image(); image.src = 'data:image/png;base64,' + base64; await image.decode();
      const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
      const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0);
      const data = ctx.getImageData(Math.ceil(bounds.x), Math.ceil(bounds.y), Math.floor(bounds.width), Math.floor(bounds.height)).data;
      let count = 0;
      for (let i = 0; i < data.length; i += 4)
        if (data[i] < 80 && data[i + 2] > data[i] + 25 && data[i + 2] > data[i + 1] + 15) count++;
      return count;
    }, { base64: pixels.toString('base64'), bounds: firstThumbnail }), 'composited thumbnail pixels');
    if (thumbnailPixels < 100) await page.waitForTimeout(100);
  }
  assert.ok(thumbnailPixels >= 100, 'The first page thumbnail must contain the rendered blue PDF header.');
  await page.screenshot({ path: `${out}/uno-desktop.png`, timeout: 10000 });
  await page.mouse.click(secondThumbnail.x + secondThumbnail.width / 2, secondThumbnail.y + secondThumbnail.height / 2);
  await page.waitForFunction(() => { const s = JSON.parse(propdfTest.State()); return s.page === 2 && s.tiles > 0; }, null, { timeout: 15000 });
  await healthy(); await command('PreviousPageButton'); assert.equal((await state()).page, 1);
  checks.push('rendered page thumbnails and pointer page navigation');
  await writeFile(`${out}/controls.json`, await bounded(page.evaluate(() => propdfTest.Controls()), 'control geometry'));
  await writeFile(`${out}/accessibility.txt`, await page.locator('body').ariaSnapshot());
  await progress('real pointer rotation');
  const rotate = await bounded(page.evaluate(() => JSON.parse(propdfTest.Controls()).find(c => c.name === 'RotateButton')), 'rotate button');
  assert.ok(rotate && rotate.width > 0 && rotate.height > 0);
  await page.mouse.click(rotate.x + rotate.width / 2, rotate.y + rotate.height / 2);
  await page.waitForFunction(revision => {
    const s = JSON.parse(propdfTest.State()); return s.error || (s.revision !== revision && !s.busy);
  }, initial.revision, { timeout: 15000 });
  await healthy(); await command('UndoButton'); assert.equal((await state()).dirty, false); checks.push('pointer page rotation and undo');
  await text('SearchQueryInput', 'workspace'); await command('SearchButton'); assert.ok((await state()).matches > 0);
  await text('SearchQueryInput', ''); await command('SearchButton'); checks.push('search and clearing highlights');
  await section(0); await command('EditObjectsButton');
  let objects = await objectsForSelection('content inspection');
  const first = objects.find(o => o.text === 'Your documents.'), second = objects.find(o => o.text === 'Your workspace.');
  assert.ok(first?.editable && second?.editable);
  await bounded(page.evaluate(({ a, b }) => { propdfTest.SelectObject(a, false); propdfTest.SelectObject(b, true); }, { a: first.index, b: second.index }), 'native list selection');
  assert.equal((await state()).selected, 2);
  await text('ContentXInput', String(Math.min(first.x, second.x) + 12)); await command('ApplyObjectBoundsButton');
  const moved = JSON.parse(await bounded(page.evaluate(() => propdfTest.Objects()), 'inspect edited objects'));
  for (const before of [first, second]) assert.ok(Math.abs(moved.find(o => o.text === before.text).x - before.x - 12) < .1);
  await command('UndoButton'); checks.push('atomic native multi-selection editing and single undo');
  objects = await objectsForSelection('objects before page alignment');
  const members = objects.filter(o => o.text === 'Your documents.' || o.text === 'Your workspace.');
  await bounded(page.evaluate(({a,b}) => { propdfTest.SelectObject(a, false); propdfTest.SelectObject(b, true); }, {a: members[0].index, b: members[1].index}), 'alignment selection');
  await bounded(page.evaluate(() => propdfTest.Expand('AlignmentSection', true)), 'expand alignment');
  await page.waitForTimeout(150);
  const alignmentPageWidth = (await state()).pageWidth;
  await bounded(page.evaluate(() => propdfTest.Choose('ContentAlignmentReferenceChoice', 1)), 'page reference');
  await bounded(page.evaluate(() => propdfTest.Choose('ContentAlignmentChoice', 1)), 'horizontal center');
  await command('AlignObjectsButton');
  const aligned = JSON.parse(await bounded(page.evaluate(() => propdfTest.Objects()), 'page-aligned PDF content')).filter(o => members.some(m => m.text === o.text));
  // Inspect resulting PDF objects against the document page size, not UI draft values.
  const left = Math.min(...aligned.map(o => o.x)), right = Math.max(...aligned.map(o => o.x + o.width));
  assert.ok(Math.abs((left + right) / 2 - alignmentPageWidth / 2) < .1);
  assert.ok(Math.abs(aligned[1].x - aligned[0].x - members[1].x + members[0].x) < .1);
  await command('UndoButton'); checks.push('page-relative group alignment through native controls and undo');
  objects = await objectsForSelection('restored objects');
  await bounded(page.evaluate(i => propdfTest.SelectObject(i, false), objects.find(o => o.text === 'Your documents.').index), 'appearance selection');
  await text('ContentFillColorInput', '#D040A0'); await command('ApplyAppearanceButton');
  await section(4); await text('ExportDpiInput', '72');
  const image = await download('ExportPngButton', 'appearance.png');
  const imageBytes = await readFile(image);
  assert.deepEqual([...imageBytes.subarray(0, 8)], [137,80,78,71,13,10,26,10]);
  const magenta = await bounded(page.evaluate(async base64 => {
    const img = new Image(); img.src = 'data:image/png;base64,' + base64; await img.decode();
    const canvas = document.createElement('canvas'); canvas.width = img.width; canvas.height = img.height;
    const ctx = canvas.getContext('2d'); ctx.drawImage(img, 0, 0);
    const data = ctx.getImageData(0, 0, img.width, img.height).data;
    let count = 0;
    for (let i = 0; i < data.length; i += 4)
      if (Math.abs(data[i] - 208) < 4 && Math.abs(data[i + 1] - 64) < 4 && Math.abs(data[i + 2] - 160) < 4) count++;
    return count;
  }, imageBytes.toString('base64')), 'independent PNG pixel check');
  assert.ok(magenta > 100, 'The exported PDF must contain the newly colored text, not only a valid PNG header.');
  checks.push('appearance edit, actual raster download and independent colored pixels'); await command('UndoButton');
  await section(5); await text('DocumentTitleInput', 'ProPDF browser round trip'); await command('SaveMetadataButton');
  assert.equal((await state()).title, 'ProPDF browser round trip');
  const pdf = await download('SaveButton', 'roundtrip.pdf'); assert.equal((await readFile(pdf)).subarray(0, 5).toString(), '%PDF-');
  assert.equal((await state()).dirty, false); checks.push('metadata editing and PDF download handoff');
  await progress('actual file picker and PDF reopen');
  const chooser = page.waitForEvent('filechooser');
  const opening = bounded(page.evaluate(() => propdfTest.Click('OpenButton')), 'PDF reopen');
  await (await chooser).setFiles(pdf); await opening; await healthy();
  assert.equal((await state()).title, 'ProPDF browser round trip'); assert.equal((await state()).pages, 3);
  checks.push('actual file picker and saved PDF reopen');
  await section(4); const txt = await download('ExportTextButton', 'document.txt');
  assert.ok((await readFile(txt, 'utf8')).includes('Your documents.')); checks.push('text export through shared pipeline');
  await page.setViewportSize({ width: 800, height: 900 }); await page.waitForTimeout(400);
  assert.ok((await state()).tiles > 0); await page.screenshot({ path: `${out}/uno-narrow.png`, timeout: 10000 }); checks.push('responsive narrow viewport');
  assert.deepEqual(errors, [], 'No unhandled JavaScript failures'); assert.deepEqual(requests, [], 'No missing runtime assets');
  await writeFile(`${out}/results.json`, JSON.stringify({ checks, state: await state(), errors, requests }, null, 2));
  console.log(`PASS: ${checks.length} Uno browser workflows.`);
} catch (error) {
  await page.screenshot({ path: `${out}/failure.png`, timeout: 4000 }).catch(() => {});
  await writeFile(`${out}/failure.json`, JSON.stringify({ checkpoint, error: String(error), stack: error.stack, errors, requests, consoleMessages, checks, state: lastState }, null, 2));
  throw error;
} finally {
  clearTimeout(watchdog);
  await bounded(browser.close(), 'browser teardown', 5000).catch(() => { process.exitCode = 1; });
}
