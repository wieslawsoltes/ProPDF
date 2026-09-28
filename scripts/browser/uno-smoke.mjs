import assert from 'node:assert/strict';
import { mkdir, readFile, writeFile } from 'node:fs/promises';
import { chromium } from 'playwright';
import { waitForReopenedDocument } from './presentation.mjs';
import { verifyWorkspaceChrome } from './workspace-ui.mjs';
import { standardFontPdf, inspectStandardFontImage, verifyDensityTransition } from './rendering-quality.mjs';
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
async function pointer(name) {
  await page.waitForFunction(name => JSON.parse(propdfTest.Controls()).some(c => c.name === name && c.visible && c.width > 0 && c.height > 0), name);
  const c = await page.evaluate(name => JSON.parse(propdfTest.Controls()).find(c => c.name === name && c.visible), name);
  await page.mouse.click(c.x + c.width / 2, c.y + c.height / 2);
}
async function section(index) {
  await progress(`inspector ${index}`);
  await pointer('AllToolsButton');
  const name = ['Edit','Review','Forms','Navigate','Export','Document','Organize','Redact'][index];
  await page.waitForFunction(() => JSON.parse(propdfTest.State()).section === 'AllTools');
  await pointer('Tool' + name + 'Button');
  await page.waitForFunction(name => JSON.parse(propdfTest.State()).section === name, name);
  // A task changes synchronously; its native ContentControl is realized on a
  // subsequent dispatcher pass. Observe the actual input, not only shell state.
  const ready = ['ContentObjectsList', 'CommentInput', 'FieldValueInput', 'NavigationBookmarksList',
    'ExportDpiInput', 'DocumentTitleInput', 'OrganizeRotateButton', 'MarkRedactionButton'][index];
  await page.waitForFunction(name => JSON.parse(propdfTest.Controls()).some(c =>
    c.name === name && c.visible && c.width > 0 && c.height > 0), ready);
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
async function openPdf(path) {
  const chooser = page.waitForEvent('filechooser');
  const opening = bounded(page.evaluate(() => propdfTest.Click('OpenButton')), 'PDF import');
  await (await chooser).setFiles(path); await opening; await healthy();
}
// Synthetic ASCII PDF with explicit font advances; no font programs or external assets.
function fallbackMetricPdf() {
  const stream = 'BT /N 40 Tf 1 0 0 1 20 130 Tm (M) Tj ET\nBT /W 40 Tf 1 0 0 1 20 55 Tm (M) Tj ET\n';
  const objects = [
    '<< /Type /Catalog /Pages 2 0 R >>',
    '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 180] /Resources << /Font << /N 4 0 R /W 6 0 R >> >> /Contents 7 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /ProPDFMissingTypeface /Encoding /WinAnsiEncoding /FirstChar 77 /LastChar 77 /Widths [200] /FontDescriptor 5 0 R >>',
    '<< /Type /FontDescriptor /FontName /ProPDFMissingTypeface /Flags 32 /FontBBox [0 -200 1000 900] /ItalicAngle 0 /Ascent 800 /Descent -200 /CapHeight 700 /StemV 80 >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /ProPDFMissingTypeface /Encoding /WinAnsiEncoding /FirstChar 77 /LastChar 77 /Widths [800] /FontDescriptor 5 0 R >>',
    `<< /Length ${Buffer.byteLength(stream)} >>\nstream\n${stream}endstream`];
  let output = '%PDF-1.7\n'; const offsets = [0];
  objects.forEach((value, i) => { offsets.push(Buffer.byteLength(output)); output += `${i + 1} 0 obj\n${value}\nendobj\n`; });
  const xref = Buffer.byteLength(output);
  output += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (const offset of offsets.slice(1)) output += `${String(offset).padStart(10, '0')} 00000 n \n`;
  return Buffer.from(output + `trailer\n<< /Size ${objects.length + 1} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`);
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
  const fonts = JSON.parse(await bounded(page.evaluate(()=>propdfTest.FontDiagnostics()),'native font diagnostics'));
  await writeFile(`${out}/font-diagnostics.json`,JSON.stringify(fonts,null,2));
  assert.equal(fonts.suppliedFaces,4); assert.equal(fonts.regular,'Open Sans'); assert.equal(fonts.bold,'Open Sans');
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
  await pointer('PagesPaneButton');
  await page.waitForFunction(() => JSON.parse(propdfTest.State()).pagesOpen);
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
  await pointer('FindPaneButton');
  await page.waitForFunction(() => JSON.parse(propdfTest.State()).searchOpen);
  await text('SearchQueryInput', 'workspace'); await command('SearchButton'); assert.ok((await state()).matches > 0);
  await text('SearchQueryInput', ''); await command('SearchButton'); checks.push('search and clearing highlights');
  await pointer('CloseSearchButton');
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
  await section(0); await command('EditObjectsButton');
  objects = await objectsForSelection('objects before typography replacement');
  await bounded(page.evaluate(i => propdfTest.SelectObject(i, false), objects.find(o => o.text === 'Your documents.').index), 'typography selection');
  await bounded(page.evaluate(() => propdfTest.Expand('TextSection', true)), 'expand typography');
  await page.waitForFunction(() => JSON.parse(propdfTest.Controls()).some(c => c.name === 'ContentStandardFontChoice'));
  await bounded(page.evaluate(() => propdfTest.Choose('ContentStandardFontChoice', 5)), 'choose Courier-Bold');
  await text('ContentLineSpacingInput', '1.6'); await text('ContentFontSizeInput', '18');
  await text('ContentWidthInput', '300'); await text('ContentHeightInput', '100');
  await text('ContentTextInput', 'First row\nSecond row');
  await command('ReplaceObjectTextButton');
  const typedObjects = JSON.parse(await bounded(page.evaluate(() => propdfTest.Objects()), 'independent typography objects'));
  const line1 = typedObjects.find(o => o.text === 'First row'), line2 = typedObjects.find(o => o.text === 'Second row');
  assert.ok(line1 && line2); assert.ok(Math.abs(line2.y - line1.y - 28.8) < .1);
  // Courier has a 600-unit advance. The first line has nine characters at 18 points.
  assert.ok(Math.abs(line1.width - 97.2) < 1, 'The native face control must affect the actual PDF text metrics.');
  await command('UndoButton'); assert.equal((await state()).dirty, false);
  await bounded(page.evaluate(() => propdfTest.Choose('ContentStandardFontChoice', 0)), 'restore text face');
  await text('ContentLineSpacingInput', '1.2'); await text('ContentFontSizeInput', '14');
  checks.push('native typography controls, replacement line spacing/font metrics and atomic undo');
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
  await progress('independent substitute-font width fixture');
  const metricPdf = `${out}/fallback-metrics.pdf`; await writeFile(metricPdf, fallbackMetricPdf());
  await openPdf(metricPdf); assert.equal((await state()).pages, 1);
  await section(4); await text('ExportDpiInput', '72');
  const metricPng = await download('ExportPngButton', 'fallback-metrics.png');
  const ink = await bounded(page.evaluate(async base64 => {
    const img = new Image(); img.src = 'data:image/png;base64,' + base64; await img.decode();
    const canvas = document.createElement('canvas'); canvas.width = img.width; canvas.height = img.height;
    const ctx = canvas.getContext('2d'); ctx.drawImage(img, 0, 0);
    const pixels = ctx.getImageData(0, 0, img.width, img.height).data;
    function bounds(top, bottom) {
      let minX = img.width, maxX = -1, minY = bottom, maxY = -1;
      for (let y = top; y < bottom; y++) for (let x = 0; x < img.width; x++) {
        const i = (y * img.width + x) * 4;
        if (pixels[i] < 128 && pixels[i + 1] < 128 && pixels[i + 2] < 128) {
          minX = Math.min(minX, x); maxX = Math.max(maxX, x); minY = Math.min(minY, y); maxY = Math.max(maxY, y);
        }
      }
      return { width: maxX - minX + 1, height: maxY - minY + 1 };
    }
    return [bounds(0, 75), bounds(80, 170)];
  }, (await readFile(metricPng)).toString('base64')), 'independent fallback glyph width pixels');
  assert.ok(ink[0].width > 1 && ink[1].width / ink[0].width > 3 && ink[1].width / ink[0].width < 5);
  assert.ok(Math.abs(ink[0].height - ink[1].height) <= 2);
  checks.push('real PDF import/export respects explicit substitute-font widths without distorting glyph height');
  await progress('standard sans punctuation shape and real bold outlines');
  const standardPdf=`${out}/standard-font-quality.pdf`;await writeFile(standardPdf,standardFontPdf());
  await openPdf(standardPdf);await section(4);await text('ExportDpiInput','72');
  const standardPng=await download('ExportPngButton','standard-font-quality.png');
  const standardInk=await inspectStandardFontImage(page,await readFile(standardPng));
  await writeFile(`${out}/standard-font-quality.json`,JSON.stringify(standardInk,null,2));
  checks.push('correct punctuation proportions and distinct bold outline through actual PDF import/export');
  await openPdf(pdf); assert.equal((await state()).pages, 3);
  assert.ok((await bounded(page.evaluate(() => propdfTest.TextContent()), 'reopened document text')).includes('Your documents.'));
  if ((await state()).pagesOpen) await pointer('PagesPaneButton');
  await pointer('CloseToolsButton');
  await page.setViewportSize({ width: 800, height: 900 });
  await progress('reopened document compositor presentation');
  const presentation = await waitForReopenedDocument(page, `${out}/uno-narrow.png`);
  await writeFile(`${out}/presentation.json`, JSON.stringify(presentation, null, 2));
  assert.ok((await state()).tiles > 0); checks.push('responsive narrow viewport presents the reopened PDF, not the previous fixture');
  await verifyWorkspaceChrome(page, out, state, pointer);
  checks.push('task catalog, quick tools, panel dismissal, compact overlay drawers and unchanged PDF revision');
  await progress('high-DPI viewport and thumbnail density-only transition');
  await verifyDensityTransition(browser,base,out);
  checks.push('Retina and fractional-density viewport/thumbnail rasterization with density-only changes');
  assert.deepEqual(errors, [], 'No unhandled JavaScript failures'); assert.deepEqual(requests, [], 'No missing runtime assets');
  await writeFile(`${out}/results.json`, JSON.stringify({ checks, state: await state(), errors, requests }, null, 2));
  console.log(`PASS: ${checks.length} Uno browser workflows.`);
} catch (error) {
  await page.screenshot({ path: `${out}/failure.png`, timeout: 4000 }).catch(() => {});
  const finalState = await state().catch(() => lastState);
  const finalControls = await page.evaluate(() => JSON.parse(propdfTest.Controls())).catch(() => []);
  await writeFile(`${out}/failure-controls.json`, JSON.stringify(finalControls, null, 2));
  await writeFile(`${out}/failure.json`, JSON.stringify({ checkpoint, error: String(error), stack: error.stack, errors, requests, consoleMessages, checks, state: finalState }, null, 2));
  throw error;
} finally {
  clearTimeout(watchdog);
  await bounded(browser.close(), 'browser teardown', 5000).catch(() => { process.exitCode = 1; });
}
