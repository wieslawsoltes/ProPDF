import assert from 'node:assert/strict';

const coordinates = ['x', 'y', 'width', 'height'];
export function sameRectangle(a, b) {
  return !!a && !!b && coordinates.every(key => Math.abs(a[key] - b[key]) <= 0.01);
}
function actionable(snapshot, name) {
  const matches = snapshot.controls.filter(c => c.name === name && c.visible);
  if (matches.length !== 1) return null;
  const c = matches[0];
  if (!coordinates.every(key => Number.isFinite(c[key])) || c.width <= 0 || c.height <= 0) return null;
  const x = c.x + c.width / 2, y = c.y + c.height / 2;
  if (!(x >= 0 && x < snapshot.width && y >= 0 && y < snapshot.height)) return null;
  return { x: c.x, y: c.y, width: c.width, height: c.height };
}

/** Observe completed animation frames, not synchronous Visibility with old layout. */
export async function waitForStableControl(name, nextFrame, {
  stableFrames = 3, maximumFrames = 120, trace = []
} = {}) {
  assert.ok(typeof name === 'string' && name.length > 0);
  assert.ok(Number.isInteger(stableFrames) && stableFrames >= 2 && stableFrames <= 10);
  assert.ok(Number.isInteger(maximumFrames) && maximumFrames >= stableFrames && maximumFrames <= 1200);
  let previous = null, stable = 0;
  for (let frame = 0; frame < maximumFrames; frame++) {
    const candidate = actionable(await nextFrame(), name);
    if (!sameRectangle(candidate, previous) && trace.length < 512)
      trace.push({ name, frame, bounds: candidate });
    stable = candidate && sameRectangle(candidate, previous) ? stable + 1 : candidate ? 1 : 0;
    previous = candidate;
    if (stable >= stableFrames) return candidate;
  }
  throw new Error(`Native control did not reach stable, visible, on-screen geometry: ${name}`);
}

/** One physical click only. Never re-execute a command to disguise a missed click. */
export async function clickNativeControl(page, name, trace = []) {
  const deadline = Date.now() + 20000;
  async function nextFrame() {
    const remaining = deadline - Date.now();
    if (remaining <= 0) throw new Error(`Pointer layout deadline exceeded: ${name}`);
    let timer;
    try {
      return await Promise.race([
        page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => resolve({
          controls: JSON.parse(propdfTest.Controls()), width: innerWidth, height: innerHeight
        })))),
        new Promise((_, reject) => { timer = setTimeout(() => reject(new Error(`Native frame stalled: ${name}`)), remaining); })
      ]);
    } finally { clearTimeout(timer); }
  }
  // Moving the pointer can change hover layout. Observe again before the click,
  // without calling UpdateLayout, mutating the visual tree or forcing a refresh.
  for (let attempt = 0; attempt < 4; attempt++) {
    const before = await waitForStableControl(name, nextFrame, { trace });
    await page.mouse.move(before.x + before.width / 2, before.y + before.height / 2);
    const after = await waitForStableControl(name, nextFrame, { trace });
    if (!sameRectangle(before, after)) continue;
    if (trace.length < 512) trace.push({ name, click: { x: after.x + after.width / 2, y: after.y + after.height / 2 } });
    await page.mouse.click(after.x + after.width / 2, after.y + after.height / 2);
    return;
  }
  throw new Error(`Native control moved repeatedly on hover: ${name}`);
}
