import test from 'node:test';
import assert from 'node:assert/strict';
import { saveFile } from '../../samples/ProPDF.Avalonia.Viewer.Browser/wwwroot/file-save.mjs';

function fixture(fail = false) {
  const state = { clicks: 0, removed: 0, revoked: [], timers: [] };
  const anchor = { style: {}, click() { state.clicks++; if (fail) throw new Error('download refused'); }, remove() { state.removed++; } };
  const host = { atob, Blob, URL: { createObjectURL(blob) { state.blob = blob; return 'blob:test'; }, revokeObjectURL(url) { state.revoked.push(url); } },
    document: { createElement(type) { assert.equal(type, 'a'); return anchor; }, body: { appendChild(value) { assert.equal(value, anchor); } } },
    setTimeout(action, delay) { state.timers.push({ action, delay }); } };
  return { state, anchor, host };
}

test('hands actual bytes to a browser download without a network request', async () => {
  const { state, anchor, host } = fixture();
  saveFile(btoa('%PDF-1.7\n'), 'document.pdf', host);
  assert.equal(await state.blob.text(), '%PDF-1.7\n');
  assert.equal(state.blob.type, 'application/pdf');
  assert.equal(anchor.download, 'document.pdf');
  assert.equal(anchor.href, 'blob:test');
  assert.equal(state.clicks, 1);
  assert.equal(state.removed, 1);
});
test('releases the object URL after the browser has consumed the click', () => {
  const { state, host } = fixture();
  saveFile(btoa('hello'), 'hello.txt', host);
  assert.deepEqual(state.revoked, []);
  assert.equal(state.timers.length, 1);
  assert.equal(state.timers[0].delay, 30000);
  state.timers[0].action();
  assert.deepEqual(state.revoked, ['blob:test']);
});
test('cleans up and propagates a refused handoff', () => {
  const { state, host } = fixture(true);
  assert.throws(() => saveFile(btoa('data'), 'file.pdf', host), /download refused/);
  assert.deepEqual(state.revoked, ['blob:test']);
  assert.equal(state.removed, 1);
  assert.equal(state.timers.length, 0);
});
test('strips paths and control characters from download names', () => {
  const { anchor, host } = fixture();
  saveFile(btoa('data'), '../private\\report\u0000.pdf', host);
  assert.equal(anchor.download, 'report.pdf');
});
test('replaces empty and dot-only download names', () => {
  for (const value of ['', '..', '/', null]) {
    const { anchor, host } = fixture();
    saveFile('', value, host);
    assert.equal(anchor.download, 'document.pdf');
  }
});
test('preserves Unicode filenames and text byte encoding', async () => {
  const { state, anchor, host } = fixture();
  const bytes = new TextEncoder().encode('Zażółć gęślą jaźń');
  saveFile(Buffer.from(bytes).toString('base64'), 'Przegląd.txt', host);
  assert.equal(anchor.download, 'Przegląd.txt');
  assert.equal(await state.blob.text(), 'Zażółć gęślą jaźń');
});
test('selects conservative MIME types for exported files', () => {
  for (const [name, mime] of [['page.PNG', 'image/png'], ['page.jpg', 'image/jpeg'], ['export.bin', 'application/octet-stream']]) {
    const { state, host } = fixture();
    saveFile('', name, host);
    assert.equal(state.blob.type, mime);
  }
});
test('rejects invalid data before allocating an output URL', () => {
  const { state, host } = fixture();
  assert.throws(() => saveFile(null, 'file.pdf', host), /limited/);
  assert.throws(() => saveFile('%%%', 'file.pdf', host));
  assert.equal(state.blob, undefined);
  assert.equal(state.clicks, 0);
});
