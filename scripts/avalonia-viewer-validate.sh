#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="$PWD/artifacts/avalonia-viewer"
mkdir -p "$OUT"
node --test scripts/tests/viewer-file-save.test.mjs | tee "$OUT/file-save-js.log"
dotnet build samples/ProPDF.Avalonia.Viewer.Desktop/ProPDF.Avalonia.Viewer.Desktop.csproj -c Release -p:ProPDFBuildBrowser=false | tee "$OUT/desktop-build.log"
dotnet test tests/ProPDF.Tests/ProPDF.Tests.csproj -c Release -p:ProPDFBuildBrowser=false --logger trx --results-directory "$OUT/core-tests" | tee "$OUT/core-tests.log"
dotnet workload install wasm-tools | tee "$OUT/wasm-workload.log"
dotnet publish samples/ProPDF.Avalonia.Viewer.Browser/ProPDF.Avalonia.Viewer.Browser.csproj -c Release -p:ProPDFBuildBrowser=false -o "$OUT/browser-publish" | tee "$OUT/browser-build.log"
find "$OUT/browser-publish" -maxdepth 3 -type f | sort > "$OUT/published-files.txt"
BROWSER_ROOT=$(python3 - "$OUT/browser-publish" <<'PY'
import sys
from pathlib import Path
roots = [Path(sys.argv[1]), Path('samples/ProPDF.Avalonia.Viewer.Browser/bin/Release')]
for root in roots:
    for index in sorted(root.rglob('index.html')):
        runtime = index.parent / '_framework'
        if runtime.is_dir() and any(runtime.glob('dotnet*.js')):
            print(index.parent.resolve())
            raise SystemExit(0)
raise SystemExit('No complete published WebAssembly application was found. See published-files.txt.')
PY
)
mkdir -p "$OUT/browser"
cp -a "$BROWSER_ROOT/." "$OUT/browser/"
python3 - "$OUT/browser/build.json" <<'PY'
import json, subprocess, sys
from pathlib import Path
run=lambda *args: subprocess.check_output(args,text=True).strip()
Path(sys.argv[1]).write_text(json.dumps({'commit':run('git','rev-parse','HEAD'),'tree':run('git','rev-parse','HEAD^{tree}'),'sdk':run('dotnet','--version')},indent=2))
PY
rm -rf "$OUT/browser-publish"
python3 -m pip install --disable-pip-version-check playwright==1.57.0 Pillow==12.0.0
python3 -m playwright install --with-deps chromium
python3 scripts/avalonia-viewer-server.py --directory "$OUT/browser" --port 8123 > "$OUT/server.log" 2>&1 &
SERVER_PID=$!
trap 'kill "$SERVER_PID" 2>/dev/null || true' EXIT
python3 scripts/avalonia-viewer-browser.py --url 'http://127.0.0.1:8123/?diagnostics=1' --output "$OUT/browser-dpr1" --dpr 1
python3 scripts/avalonia-viewer-browser.py --url 'http://127.0.0.1:8123/?diagnostics=1' --output "$OUT/browser-dpr2" --dpr 2
find "$OUT" -type f ! -name SHA256SUMS.txt -print0 | sort -z | xargs -0 sha256sum > "$OUT/SHA256SUMS.txt"
