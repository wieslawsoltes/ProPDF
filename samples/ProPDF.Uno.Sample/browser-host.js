// ProPDF-owned browser boundary. Document bytes stay in this tab; no upload, analytics or automatic persistence.
let dirty = false;
export function setDirty(value) { dirty = value; }
window.addEventListener('beforeunload', event => { if (dirty) { event.preventDefault(); event.returnValue = ''; } });
export function failure(error) {
    document.documentElement.dataset.propdfError = error; console.error(error);
    let panel = document.getElementById('propdf-startup-error');
    if (!panel) {
        panel = document.createElement('pre'); panel.id = 'propdf-startup-error'; panel.setAttribute('role', 'alert');
        panel.style.cssText = 'position:fixed;inset:24px;z-index:2147483647;padding:24px;overflow:auto;white-space:pre-wrap;background:white;color:#12213d;font:14px/1.5 system-ui;border:1px solid #cbd3e0';
        document.body.append(panel);
    }
    panel.textContent = 'ProPDF could not initialize. Reload to retry.\n\n' + error;
}
export async function ready() {
    document.documentElement.dataset.propdfReady = 'true';
    if (new URL(location.href).searchParams.get('test') === '1') {
        const runtime = globalThis.getDotnetRuntime(0);
        const exports = await runtime.getAssemblyExports('ProPDF.Uno.Sample');
        globalThis.propdfTest = exports.ProPDF.Uno.Sample.BrowserTest;
    }
}
export function pickFile(accept, maximumBytes) {
    return new Promise((resolve, reject) => {
        const input = document.createElement('input'); input.type = 'file'; input.accept = accept;
        input.style.display = 'none'; document.body.append(input);
        let completed = false;
        const finish = (value, error) => { if (completed) return; completed = true; input.remove(); error ? reject(error) : resolve(value); };
        input.addEventListener('cancel', () => finish(null), { once: true });
        input.addEventListener('change', async () => {
            const file = input.files?.[0]; if (!file) { finish(null); return; }
            try {
                if (file.size > maximumBytes) throw new Error('File exceeds the 32 MiB browser import limit.');
                const reader = new FileReader();
                const data = await new Promise((ok, fail) => { reader.onload = () => ok(reader.result); reader.onerror = () => fail(reader.error); reader.readAsDataURL(file); });
                finish(JSON.stringify({ name: file.name, base64: data.slice(data.indexOf(',') + 1) }));
            } catch (error) { finish(null, error); }
        }, { once: true });
        try { input.click(); } catch (error) { finish(null, error); }
    });
}
export function downloadFile(name, mime, base64) {
    if (base64.length > 90 * 1024 * 1024) throw new Error('Export exceeds the browser transfer limit.');
    const raw = atob(base64); const bytes = new Uint8Array(raw.length);
    for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
    const url = URL.createObjectURL(new Blob([bytes], { type: mime }));
    const link = document.createElement('a'); link.href = url; link.download = name; link.style.display = 'none'; document.body.append(link);
    try { link.click(); } finally { link.remove(); setTimeout(() => URL.revokeObjectURL(url), 60000); }
    // Browser acceptance is a download handoff, not proof of a durable disk write. No document bytes are logged.
}
