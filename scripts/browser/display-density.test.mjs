import assert from 'node:assert/strict';
import test from 'node:test';
import { observeDisplayDensity } from '../../samples/ProPDF.Uno.Sample/display-density.mjs';

class Query extends EventTarget {
    listeners = new Set();
    constructor(media, legacy = false) {
        super(); this.media = media;
        if (legacy) { this.addEventListener = undefined; this.removeEventListener = undefined; }
    }
    addEventListener(type, listener) { if (type === 'change') this.listeners.add(listener); }
    removeEventListener(type, listener) { if (type === 'change') this.listeners.delete(listener); }
    addListener(listener) { this.listeners.add(listener); }
    removeListener(listener) { this.listeners.delete(listener); }
    fire() { for (const listener of [...this.listeners]) listener(new Event('change')); }
}
class Host extends EventTarget {
    devicePixelRatio = 2; Event = Event; queries = []; frames = new Map(); sequence = 0; resized = 0;
    constructor(legacy = false) { super(); this.legacy = legacy; this.addEventListener('resize', () => this.resized++); }
    matchMedia(media) { const query = new Query(media, this.legacy); this.queries.push(query); return query; }
    requestAnimationFrame(fn) { const id = ++this.sequence; this.frames.set(id, fn); return id; }
    cancelAnimationFrame(id) { this.frames.delete(id); }
    flush() { const frames = [...this.frames.values()]; this.frames.clear(); for (const fn of frames) fn(); }
    changeDensity(value) { this.devicePixelRatio = value; for (const q of [...this.queries]) q.fire(); }
    hide(persisted) { const e = new Event('pagehide'); e.persisted = persisted; this.dispatchEvent(e); }
}

test('density observer installs without changing the initial frame', () => {
    const h = new Host(), stop = observeDisplayDensity(h);
    assert.equal(h.queries[0].media, '(resolution: 2dppx)');
    assert.equal(h.frames.size, 0); assert.equal(h.resized, 0); stop();
});
test('a media-only density change reaches the normal host resize path once', () => {
    const h = new Host(), stop = observeDisplayDensity(h); h.changeDensity(1.25);
    assert.equal(h.frames.size, 1); assert.equal(h.resized, 0);
    assert.equal(h.queries[0].listeners.size, 0);
    assert.equal(h.queries.at(-1).media, '(resolution: 1.25dppx)');
    h.flush(); assert.equal(h.resized, 1); assert.equal(h.frames.size, 0); stop();
});
test('rapid density transitions coalesce without retaining old media listeners', () => {
    const h = new Host(), stop = observeDisplayDensity(h);
    for (const d of [1.25, 3, 2, 1.5]) h.changeDensity(d);
    assert.equal(h.frames.size, 1); assert.equal(h.queries.filter(q => q.listeners.size).length, 1);
    assert.equal(h.queries.at(-1).media, '(resolution: 1.5dppx)');
    h.flush(); assert.equal(h.resized, 1); stop();
});
test('1000 ordinary resize events do not schedule redundant work', () => {
    const h = new Host(), stop = observeDisplayDensity(h);
    for (let i = 0; i < 1000; i++) h.dispatchEvent(new Event('resize'));
    assert.equal(h.frames.size, 0); assert.equal(h.resized, 1000); assert.equal(h.queries.length, 1); stop();
});
test('disposal cancels pending work and is idempotent', () => {
    const h = new Host(), stop = observeDisplayDensity(h); h.changeDensity(1.25);
    stop(); stop(); assert.equal(h.frames.size, 0); h.flush(); h.changeDensity(3);
    h.dispatchEvent(new Event('pageshow'));
    assert.equal(h.resized, 0); assert.equal(h.frames.size, 0);
    assert.equal(h.queries.filter(q => q.listeners.size).length, 0);
});
test('back-forward cache restoration rechecks density, normal exit disposes', () => {
    const h = new Host(); observeDisplayDensity(h); h.hide(true);
    h.devicePixelRatio = 1.25; h.dispatchEvent(new Event('pageshow')); h.flush();
    assert.equal(h.resized, 1); h.hide(false); h.changeDensity(2); h.flush();
    assert.equal(h.resized, 1); assert.equal(h.queries.filter(q => q.listeners.size).length, 0);
});
test('legacy media-query listeners are rearmed and removed', () => {
    const h = new Host(true), stop = observeDisplayDensity(h); h.changeDensity(1.25); h.flush();
    assert.equal(h.resized, 1); assert.equal(h.queries[0].listeners.size, 0); stop();
    assert.equal(h.queries.at(-1).listeners.size, 0);
});
test('invalid transient densities are ignored', () => {
    const h = new Host(), stop = observeDisplayDensity(h);
    for (const d of [0, NaN, Infinity, -1]) h.changeDensity(d);
    assert.equal(h.frames.size, 0); assert.equal(h.queries.length, 1);
    h.changeDensity(1.25); h.flush(); assert.equal(h.resized, 1); stop();
});
test('hosts without media queries retain resize and pageshow detection', () => {
    const h = new Host(); h.matchMedia = undefined;
    const stop = observeDisplayDensity(h); h.devicePixelRatio = 1.25;
    h.dispatchEvent(new Event('resize')); h.flush();
    assert.equal(h.resized, 2); assert.equal(h.frames.size, 0); stop();
});
