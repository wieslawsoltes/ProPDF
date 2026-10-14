#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
OUT="$PWD/artifacts/avalonia-viewer"
mkdir -p "$OUT"
dotnet build samples/ProPDF.Avalonia.Viewer.Desktop/ProPDF.Avalonia.Viewer.Desktop.csproj -c Release -p:ProPDFBuildBrowser=false | tee "$OUT/desktop-build.log"
dotnet test tests/ProPDF.Tests/ProPDF.Tests.csproj -c Release -p:ProPDFBuildBrowser=false --logger trx --results-directory "$OUT/core-tests" | tee "$OUT/core-tests.log"
dotnet workload install wasm-tools | tee "$OUT/wasm-workload.log"
dotnet publish samples/ProPDF.Avalonia.Viewer.Browser/ProPDF.Avalonia.Viewer.Browser.csproj -c Release -p:ProPDFBuildBrowser=false -o "$OUT/browser-publish" | tee "$OUT/browser-build.log"
INDEX=$(find "$OUT/browser-publish" -name index.html -type f | head -n 1)
if [[ -z "$INDEX" ]]; then
  INDEX=$(find samples/ProPDF.Avalonia.Viewer.Browser/bin/Release -path '*/AppBundle/index.html' -type f | head -n 1)
fi
[[ -n "$INDEX" ]] || { echo 'No published browser entry point'; exit 1; }
BROWSER_ROOT=$(dirname "$INDEX")
[[ -f "$BROWSER_ROOT/_framework/dotnet.js" ]] || { echo 'Published .NET runtime missing'; exit 1; }
mkdir -p "$OUT/browser"
cp -a "$BROWSER_ROOT/." "$OUT/browser/"
python3 - "$OUT/browser/build.json" <<'PY'
import json, subprocess, sys
from pathlib import Path
run=lambda *args: subprocess.check_output(args,text=True).strip()
Path(sys.argv[1]).write_text(json.dumps({'commit':run('git','rev-parse','HEAD'),'tree':run('git','rev-parse','HEAD^{tree}'),'sdk':run('dotnet','--version')},indent=2))
PY
python3 -m pip install --disable-pip-version-check playwright==1.57.0 Pillow==12.0.0
python3 -m playwright install --with-deps chromium
python3 scripts/avalonia-viewer-server.py --directory "$OUT/browser" --port 8123 > "$OUT/server.log" 2>&1 &
SERVER_PID=$!
trap 'kill "$SERVER_PID" 2>/dev/null || true' EXIT
python3 scripts/avalonia-viewer-browser.py --url 'http://127.0.0.1:8123/?diagnostics=1' --output "$OUT/browser-dpr1" --dpr 1
python3 scripts/avalonia-viewer-browser.py --url 'http://127.0.0.1:8123/?diagnostics=1' --output "$OUT/browser-dpr2" --dpr 2
rm -rf "$OUT/browser-publish"
find "$OUT" -type f ! -name SHA256SUMS.txt -print0 | sort -z | xargs -0 sha256sum > "$OUT/SHA256SUMS.txt"
