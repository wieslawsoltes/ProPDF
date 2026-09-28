// ProPDF-owned browser-host integration; no polling, canvas patches or private Uno APIs.
// Uno 6.7.135's BrowserRenderer sizes its canvas on window.resize, whereas its
// managed DisplayInformation can notice a density-only change independently.
// Bridge resolution media changes to the normal resize path so both agree.
export function observeDisplayDensity(host = globalThis) {
    let density = readDensity() ?? 1, query = null, frame = null, disposed = false;
    function readDensity() {
        const value = host.devicePixelRatio;
        return Number.isFinite(value) && value > 0 ? value : null;
    }
    function detachQuery() {
        if (query?.removeEventListener) query.removeEventListener('change', changed);
        else query?.removeListener?.(changed);
        query = null;
    }
    function arm() {
        detachQuery();
        query = host.matchMedia?.(`(resolution: ${density}dppx)`) ?? null;
        if (query?.addEventListener) query.addEventListener('change', changed);
        else query?.addListener?.(changed);
    }
    function changed() {
        if (disposed) return;
        const next = readDensity();
        if (next === null || next === density) return;
        density = next; arm();
        if (frame === null) frame = host.requestAnimationFrame(() => {
            frame = null;
            if (!disposed) host.dispatchEvent(new host.Event('resize'));
        });
    }
    function pageHidden(event) { if (!event.persisted) dispose(); }
    function dispose() {
        if (disposed) return;
        disposed = true; detachQuery();
        if (frame !== null) host.cancelAnimationFrame(frame);
        frame = null;
        host.removeEventListener('resize', changed);
        host.removeEventListener('pageshow', changed);
        host.removeEventListener('pagehide', pageHidden);
    }
    arm();
    host.addEventListener('resize', changed);
    host.addEventListener('pageshow', changed);
    host.addEventListener('pagehide', pageHidden);
    return dispose;
}
