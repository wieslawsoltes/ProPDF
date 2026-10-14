import { dotnet } from './_framework/dotnet.js';
import { saveFile } from './file-save.mjs';

let lastPrintUrl;
try {
  const runtime = await dotnet.withDiagnosticTracing(false).withApplicationArgumentsFromQuery().create();
  runtime.setModuleImports('viewer-host', {
    saveFile,
    printPdf(base64) {
      const target = window.open('about:blank', '_blank');
      if (!target) throw new Error('The print window was blocked. Allow this site to open its PDF print preview.');
      try {
        const raw = atob(base64);
        const bytes = Uint8Array.from(raw, ch => ch.charCodeAt(0));
        const next = URL.createObjectURL(new Blob([bytes], { type: 'application/pdf' }));
        target.opener = null;
        target.location.replace(next);
        if (lastPrintUrl) URL.revokeObjectURL(lastPrintUrl);
        lastPrintUrl = next;
      } catch (error) { target.close(); throw error; }
    }
  });
  const config = runtime.getConfig();
  const exports = await runtime.getAssemblyExports(config.mainAssemblyName);
  const readState = () => JSON.parse(exports.ProPDF.Avalonia.Viewer.Browser.Program.Snapshot());
  if (new URL(location.href).searchParams.get('diagnostics') === '1') globalThis.viewerProbe = readState;
  addEventListener('beforeunload', event => { if (readState().dirty) { event.preventDefault(); event.returnValue = ''; } });
  document.querySelector('#boot').textContent = '';
  await runtime.runMain(config.mainAssemblyName, [location.href]);
} catch (error) {
  console.error(error);
  document.querySelector('#boot').textContent = `The PDF workspace could not start: ${error.message || error}`;
}
