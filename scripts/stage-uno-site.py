#!/usr/bin/env python3
"""Stage the real Uno app, documentation and retained dependency notices together."""
import argparse
import json
import pathlib
import shutil
import subprocess
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--dist', required=True)
parser.add_argument('--out', required=True)
args = parser.parse_args()
source = pathlib.Path(args.dist)
roots = [f.parent for f in source.rglob('index.html') if (f.parent / 'uno-bootstrap.js').exists()]
if not roots:
    roots = [f.parent for f in source.rglob('index.html') if list(f.parent.glob('uno*'))]
if len(roots) != 1:
    raise SystemExit('Expected exactly one built Uno distribution, found: ' + str(roots))
subprocess.run([sys.executable, 'scripts/fetch-uno-notices.py'], check=True)
notices = pathlib.Path('artifacts/third-party-notices')
if not notices.is_dir() or not pathlib.Path('artifacts/licenses.json').is_file():
    raise SystemExit('Run the complete dependency license audit before staging the published site.')
target = pathlib.Path(args.out)
if target.exists():
    shutil.rmtree(target)
shutil.copytree(roots[0], target)
shutil.copy2('samples/ProPDF.Uno.Sample/browser-host.js', target / 'browser-host.js')
shutil.copytree('artifacts/site', target / 'docs')
shutil.copytree(notices, target / 'licenses/packages', dirs_exist_ok=True)
shutil.copytree('artifacts/upstream-notices', target / 'licenses/Uno', dirs_exist_ok=True)
shutil.copy2('artifacts/licenses.json', target / 'licenses/inventory.json')
(target / '.nojekyll').write_text('', encoding='utf-8')
text = (target / 'index.html').read_text(encoding='utf-8')
if '/ProPDF/' not in text and '<base ' not in text:
    text = text.replace('<head>', '<head><base href="/ProPDF/">', 1)
    (target / 'index.html').write_text(text, encoding='utf-8')
try:
    commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
except (OSError, subprocess.CalledProcessError):
    commit = 'local'
(target / 'build-info.json').write_text(json.dumps({'unoSdk': '6.7.30', 'uno': '6.7.135', 'sample': 'ProPDF.Uno.Sample', 'basePath': '/ProPDF/', 'commit': commit, 'licenses': 'licenses/inventory.json'}), encoding='utf-8')
print('Staged Uno application, docs and complete notices:', target)
