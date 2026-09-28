import assert from 'node:assert/strict';
import test from 'node:test';
import { clickNativeControl, sameRectangle, waitForStableControl } from './native-pointer.mjs';
const rectangle = { name: 'ToolDocumentButton', x: 14, y: 570, width: 212, height: 53, visible: true };
const snapshot = (...controls) => ({ controls, width: 1600, height: 1050 });
function frames(sequence) {
  let count = 0;
  return { next: async () => sequence[Math.min(count++, sequence.length - 1)], count: () => count };
}

test('nonzero visibility does not qualify an old retained layout rectangle', async () => {
  const stale = { ...rectangle, y: 710 };
  const f = frames([snapshot(stale), snapshot(stale), snapshot(rectangle), snapshot(rectangle), snapshot(rectangle)]);
  const trace = [];
  const result = await waitForStableControl(rectangle.name, f.next, { trace });
  assert.equal(result.y, 570); assert.equal(f.count(), 5);
  assert.deepEqual(trace.map(t => t.bounds?.y), [710, 570]);
});
test('hidden or zero-sized frames reset the stability window', async () => {
  for (const hidden of [{ ...rectangle, visible: false }, { ...rectangle, height: 0 }]) {
    const f = frames([snapshot(rectangle), snapshot(rectangle), snapshot(hidden), snapshot(rectangle)]);
    await waitForStableControl(rectangle.name, f.next);
    assert.equal(f.count(), 6);
  }
});
test('off-screen, nonfinite and duplicate visible targets are not clicked', async () => {
  for (const bad of [snapshot({ ...rectangle, x: 2000 }), snapshot({ ...rectangle, width: NaN }), snapshot(rectangle, rectangle)]) {
    await assert.rejects(waitForStableControl(rectangle.name, async () => bad, { maximumFrames: 3 }), /stable, visible, on-screen/);
  }
});
test('moving geometry times out instead of accepting its first positive dimensions', async () => {
  let x = 0;
  await assert.rejects(waitForStableControl(rectangle.name, async () => snapshot({ ...rectangle, x: x++ }), { maximumFrames: 8 }), /stable, visible, on-screen/);
});
test('invalid observation budgets are rejected', async () => {
  for (const options of [{ stableFrames: 1 }, { maximumFrames: 0 }, { maximumFrames: 1201 }])
    await assert.rejects(waitForStableControl(rectangle.name, async () => snapshot(rectangle), options));
});
test('rectangle equality includes position and both dimensions', () => {
  assert.equal(sameRectangle(null, rectangle), false);
  assert.equal(sameRectangle(rectangle, { ...rectangle }), true);
  for (const key of ['x', 'y', 'width', 'height']) assert.equal(sameRectangle(rectangle, { ...rectangle, [key]: rectangle[key] + 1 }), false);
});
test('hover movement is re-observed before exactly one physical click', async () => {
  let moved = false; const moves = [], clicks = [];
  const page = {
    evaluate: async () => snapshot({ ...rectangle, y: moved ? 580 : 570 }),
    mouse: { move: async (x, y) => { moves.push({ x, y }); moved = true; }, click: async (x, y) => { clicks.push({ x, y }); } }
  };
  await clickNativeControl(page, rectangle.name);
  assert.equal(moves.length, 2); assert.deepEqual(clicks, [{ x: 120, y: 606.5 }]);
});
test('an absent target cannot generate a physical click', async () => {
  let clicked = 0;
  const page = { evaluate: async () => snapshot(), mouse: { move: async () => {}, click: async () => { clicked++; } } };
  await assert.rejects(clickNativeControl(page, rectangle.name), /stable, visible, on-screen/);
  assert.equal(clicked, 0);
});
