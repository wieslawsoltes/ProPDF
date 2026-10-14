// Explicit user-requested file output. This uses the browser download mechanism,
// not native filesystem handles, and never overrides a denied native permission.
const maximumBytes = 128 * 1024 * 1024;
const mimeTypes = { pdf: 'application/pdf', png: 'image/png', jpg: 'image/jpeg', jpeg: 'image/jpeg', txt: 'text/plain;charset=utf-8' };

export function saveFile(base64, suggestedName, host = globalThis) {
  if (typeof base64 !== 'string' || base64.length > Math.ceil(maximumBytes / 3) * 4)
    throw new Error('Browser downloads are limited to 128 MiB.');
  const raw = host.atob(base64);
  if (raw.length > maximumBytes) throw new Error('Browser downloads are limited to 128 MiB.');
  const bytes = Uint8Array.from(raw, ch => ch.charCodeAt(0));
  let name = String(suggestedName || 'document.pdf').split(/[\\/]/).pop().replace(/[\x00-\x1f\x7f]/g, '').trim();
  if (!name || /^\.+$/.test(name)) name = 'document.pdf';
  const extension = name.split('.').pop().toLowerCase();
  const url = host.URL.createObjectURL(new host.Blob([bytes], { type: mimeTypes[extension] || 'application/octet-stream' }));
  let anchor;
  try {
    anchor = host.document.createElement('a');
    anchor.href = url;
    anchor.download = name;
    anchor.rel = 'noopener';
    anchor.style.display = 'none';
    host.document.body.appendChild(anchor);
    anchor.click();
    // Keep the object URL alive while the browser consumes the click. This signals
    // handoff only; the browser cannot report whether the user retained the file.
    host.setTimeout(() => host.URL.revokeObjectURL(url), 30000);
  } catch (error) {
    host.URL.revokeObjectURL(url);
    throw error;
  } finally {
    anchor?.remove();
  }
}
