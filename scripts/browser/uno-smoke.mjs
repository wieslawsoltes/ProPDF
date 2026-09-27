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
  await page.waitForFunction(() => { const s = JSON.parse(propdfTest.State()); return s.tiles > 0 && s.fields > 0; }, null, { timeout: 30000 });
  const initial = await healthy(); assert.equal(initial.pages, 3); assert.equal(initial.dirty, false);
  assert.ok(await page.locator('canvas').count() > 0); checks.push('real Uno/Skia application and PDF tiles');
  const original = await bounded(page.evaluate(() => propdfTest.TextContent()), 'independent text extraction');
  assert.ok(original.includes('Your documents.')); checks.push('independent PDF text extraction');
  await page.screenshot({ path: `${out}/uno-desktop.png`, timeout: 10000 });
  await writeFile(`${out}/controls.json`, await bounded(page.evaluate(() => propdfTest.Controls()), 'control geometry'));
  await writeFile(`${out}/accessibility.txt`, await page.locator('body').ariaSnapshot());
  await progress('real pointer rotation');
  const rotate = await bounded(page.evaluate(() => JSON.parse(propdfTest.Controls()).find(c => c.name === 'RotateButton')), 'rotate button');
  assert.ok(rotate && rotate.width > 0 && rotate.height > 0);
  await page.mouse.click(rotate.x + rotate.width / 2, rotate.y + rotate.height / 2);
  await page.waitForFunction(revision => JSON.parse(propdfTest.State()).revision !== revision, initial.revision, { timeout: 15000 });
  await healthy(); await command('UndoButton'); assert.equal((await state()).dirty, false); checks.push('pointer page rotation and undo');
  await text('SearchQueryInput', 'workspace'); await command('SearchButton'); assert.ok((await state()).matches > 0);
  await text('SearchQueryInput', ''); await command('SearchButton'); checks.push('search and clearing highlights');
  await section(0); await command('EditObjectsButton');
  let objects = JSON.parse(await bounded(page.evaluate(() => propdfTest.Objects()), 'content inspection'));
  const first = objects.find(o => o.text === 'Your documents.'), second = objects.find(o => o.text === 'Your workspace.');
  assert.ok(first?.editable && second?.editable);
  await bounded(page.evaluate(({ a, b }) => { propdfTest.SelectObject(a, false); propdfTest.SelectObject(b, true); }, { a: first.index, b: second.index }), 'native list selection');
  assert.equal((await state()).selected, 2);
  await text('ContentXInput', String(Math.min(first.x, second.x) + 12)); await command('ApplyObjectBoundsButton');
  const moved = JSON.parse(await bounded(page.evaluate(() => propdfTest.Objects()), 'inspect edited objects'));
  for (const before of [first, second]) assert.ok(Math.abs(moved.find(o => o.text === before.text).x - before.x - 12) < .1);
  await command('UndoButton'); checks.push('atomic native multi-selection editing and single undo');
  objects = JSON.parse(await bounded(page.evaluate(() => propdfTest.Objects()), 'restored objects'));
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
