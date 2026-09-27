#!/usr/bin/env python3
"""Stage the actual Uno dist at the project-site base path, with docs alongside it."""
import argparse, json, pathlib, shutil
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
target = pathlib.Path(args.out)
if target.exists(): shutil.rmtree(target)
shutil.copytree(roots[0], target)
shutil.copy2('samples/ProPDF.Uno.Sample/browser-host.js', target / 'browser-host.js')
shutil.copytree('artifacts/site', target / 'docs')
(target / '.nojekyll').write_text('')
# Project pages are served under /ProPDF/, including local test hosting.
text = (target / 'index.html').read_text()
if '/ProPDF/' not in text and '<base ' not in text:
    text = text.replace('<head>', '<head><base href="/ProPDF/">', 1)
    (target / 'index.html').write_text(text)
(target / 'build-info.json').write_text(json.dumps({'unoSdk':'6.7.30','sample':'ProPDF.Uno.Sample','basePath':'/ProPDF/'}))
print('Staged Uno application:', target)
