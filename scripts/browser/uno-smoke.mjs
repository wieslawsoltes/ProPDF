import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';
const base = process.env.PROPDF_URL || 'http://127.0.0.1:4173/ProPDF/';
const out = process.env.PROPDF_QA || 'artifacts/browser';
await mkdir(out, {recursive: true});
const browser = await chromium.launch({args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader']});
const context = await browser.newContext({viewport: {width: 1600, height: 1050}, acceptDownloads: true});
const page = await context.newPage();
const errors = []; const requests = []; const checks = [];
page.on('pageerror', e => errors.push(e.message));
page.on('console', e => { if (e.type() === 'error') console.error('Browser:', e.text()); });
page.on('response', r => { if (r.status() >= 400 && /\.(wasm|js|dll|json|ttf|woff2?)(\?|$)/i.test(r.url())) requests.push(`${r.status()} ${r.url()}`); });
page.on('dialog', async dialog => dialog.type() === 'beforeunload' ? dialog.accept() : dialog.dismiss());
async function state() { return page.evaluate(() => JSON.parse(propdfTest.State())); }
async function command(name) { await page.evaluate(name => propdfTest.Click(name), name); await healthy(); }
async function text(name, value) { await page.evaluate(({name, value}) => propdfTest.Text(name, value), {name, value}); }
async function healthy() {
  const current = await state(); assert.equal(current.error, null); assert.equal(current.busy, false); return current;
}
async function section(index) { await page.evaluate(index => propdfTest.Choose('InspectorSection', index), index); await page.waitForTimeout(120); }
async function download(name, filename) {
  const pending = page.waitForEvent('download'); await command(name); const file = await pending;
  const path = `${out}/${filename || file.suggestedFilename()}`; await file.saveAs(path); assert.equal(await file.failure(), null); return path;
}
try {
  await page.goto(base + '?test=1', {waitUntil: 'domcontentloaded'});
  await page.waitForFunction(() => globalThis.propdfTest || document.documentElement.dataset.propdfError, null, {timeout: 240000});
  const failure = await page.evaluate(() => document.documentElement.dataset.propdfError);
  assert.equal(failure, undefined);
  await page.waitForFunction(() => { const s = JSON.parse(propdfTest.State()); return s.tiles > 0 && s.fields > 0; }, null, {timeout: 30000});
  const initial = await healthy(); assert.equal(initial.pages, 3); assert.equal(initial.dirty, false);
  assert.ok(await page.locator('canvas').count() > 0); checks.push('real Uno/Skia application and PDF tiles');
  const original = await page.evaluate(() => propdfTest.TextContent()); assert.ok(original.includes('Your documents.')); checks.push('independent PDF text extraction');
  await page.screenshot({path: `${out}/uno-desktop.png`});
  await writeFile(`${out}/controls.json`, await page.evaluate(() => propdfTest.Controls()));
  await writeFile(`${out}/accessibility.txt`, await page.locator('body').ariaSnapshot());
  // A real pointer action on the native command button, not merely a bridge invocation.
  const rotate = await page.evaluate(() => JSON.parse(propdfTest.Controls()).find(c => c.name === 'RotateButton'));
  assert.ok(rotate && rotate.width > 0 && rotate.height > 0);
  await page.mouse.click(rotate.x + rotate.width / 2, rotate.y + rotate.height / 2);
  await page.waitForFunction(revision => JSON.parse(propdfTest.State()).revision !== revision, initial.revision, {timeout: 15000});
  await healthy(); await command('UndoButton'); assert.equal((await state()).dirty, false); checks.push('pointer page rotation and undo');
  await text('SearchQueryInput', 'workspace'); await command('SearchButton'); assert.ok((await state()).matches > 0);
  await text('SearchQueryInput', ''); await command('SearchButton'); checks.push('search and clearing highlights');
  await section(0); await command('EditObjectsButton');
  let objects = await page.evaluate(() => propdfTest.Objects()); objects = JSON.parse(objects);
  const first = objects.find(o => o.text === 'Your documents.'); const second = objects.find(o => o.text === 'Your workspace.');
  assert.ok(first?.editable && second?.editable);
  await page.evaluate(({a, b}) => { propdfTest.SelectObject(a, false); propdfTest.SelectObject(b, true); }, {a: first.index, b: second.index});
  assert.equal((await state()).selected, 2);
  await text('ContentXInput', String(Math.min(first.x, second.x) + 12)); await command('ApplyObjectBoundsButton');
  const moved = JSON.parse(await page.evaluate(() => propdfTest.Objects()));
  for (const before of [first, second]) assert.ok(Math.abs(moved.find(o => o.text === before.text).x - before.x - 12) < .1);
  await command('UndoButton'); checks.push('atomic native multi-selection editing and single undo');
  objects = JSON.parse(await page.evaluate(() => propdfTest.Objects()));
  await page.evaluate(i => propdfTest.SelectObject(i, false), objects.find(o => o.text === 'Your documents.').index);
  await text('ContentFillColorInput', '#D040A0'); await command('ApplyAppearanceButton');
  await section(4); await text('ExportDpiInput', '72');
  const image = await download('ExportPngButton', 'appearance.png');
  assert.deepEqual([...(await readFile(image)).subarray(0, 8)], [137,80,78,71,13,10,26,10]);
  checks.push('native appearance edit and actual raster download'); await command('UndoButton');
  await section(5); await text('DocumentTitleInput', 'ProPDF browser round trip'); await command('SaveMetadataButton');
  assert.equal((await state()).title, 'ProPDF browser round trip');
  const pdf = await download('SaveButton', 'roundtrip.pdf'); assert.equal((await readFile(pdf)).subarray(0,5).toString(), '%PDF-');
  assert.equal((await state()).dirty, false); checks.push('native metadata editing and PDF download handoff');
  const chooser = page.waitForEvent('filechooser');
  const opening = page.evaluate(() => propdfTest.Click('OpenButton'));
  await (await chooser).setFiles(pdf); await opening; await healthy();
  assert.equal((await state()).title, 'ProPDF browser round trip'); assert.equal((await state()).pages, 3);
  checks.push('actual file picker and saved PDF reopen');
  await section(4); const txt = await download('ExportTextButton', 'document.txt');
  assert.ok((await readFile(txt, 'utf8')).includes('Your documents.')); checks.push('text export through shared pipeline');
  await page.setViewportSize({width: 800, height: 900}); await page.waitForTimeout(400);
  assert.ok((await state()).tiles > 0); await page.screenshot({path: `${out}/uno-narrow.png`}); checks.push('responsive narrow viewport');
  assert.deepEqual(errors, [], 'No unhandled JavaScript failures'); assert.deepEqual(requests, [], 'No missing runtime assets');
  await writeFile(`${out}/results.json`, JSON.stringify({checks, state: await state(), errors, requests}, null, 2));
  console.log(`PASS: ${checks.length} Uno browser workflows.`);
} catch (error) {
  await page.screenshot({path: `${out}/failure.png`}).catch(() => {});
  await writeFile(`${out}/failure.json`, JSON.stringify({error: String(error), stack: error.stack, errors, requests, checks, state: await state().catch(() => null)}, null, 2));
  throw error;
} finally { await browser.close(); }
