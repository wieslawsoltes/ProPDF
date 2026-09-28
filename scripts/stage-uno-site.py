#!/usr/bin/env python3
"""Stage the real Uno app, documentation and retained dependency notices together."""
from __future__ import annotations
import argparse
import json
import pathlib
import shutil
import subprocess
import sys


def find_distribution(source: pathlib.Path) -> pathlib.Path:
    # Uno 6.7 places bootstrap scripts inside a content-hashed package_* folder,
    # alongside (not inside) the root index and _framework runtime assets.
    roots = []
    for index in source.rglob('index.html'):
        root = index.parent
        scripts = [root / 'uno-bootstrap.js', *root.glob('package_*/uno-bootstrap.js')]
        if (root / '_framework').is_dir() and any(script.is_file() for script in scripts):
            roots.append(root)
    if len(roots) != 1:
        raise ValueError('Expected exactly one built Uno distribution, found: ' + str(roots))
    return roots[0]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--dist', required=True)
    parser.add_argument('--out', required=True)
    args = parser.parse_args()
    source = pathlib.Path(args.dist).resolve()
    root = find_distribution(source)
    target = pathlib.Path(args.out).resolve()
    if source == target or source.is_relative_to(target):
        raise ValueError('Output cannot replace the input or its ancestor directory.')
    subprocess.run([sys.executable, 'scripts/fetch-uno-notices.py'], check=True)
    notices = pathlib.Path('artifacts/third-party-notices')
    if not notices.is_dir() or not pathlib.Path('artifacts/licenses.json').is_file():
        raise ValueError('Run the complete dependency license audit before staging the site.')
    if target.exists():
        shutil.rmtree(target)
    shutil.copytree(root, target)
    for module in ('browser-host.js', 'display-density.mjs'):
        shutil.copy2(pathlib.Path('samples/ProPDF.Uno.Sample') / module, target / module)
    shutil.copytree('artifacts/site', target / 'docs')
    shutil.copytree(notices, target / 'licenses/packages', dirs_exist_ok=True)
    shutil.copytree('artifacts/upstream-notices', target / 'licenses/Uno', dirs_exist_ok=True)
    shutil.copy2('artifacts/licenses.json', target / 'licenses/inventory.json')
    (target / '.nojekyll').write_text('', encoding='utf-8')
    text = (target / 'index.html').read_text(encoding='utf-8')
    if '<base ' not in text:
        text = text.replace('<head>', '<head><base href="/ProPDF/">', 1)
        (target / 'index.html').write_text(text, encoding='utf-8')
    try:
        commit = subprocess.check_output(['git', 'rev-parse', 'HEAD'], text=True).strip()
    except (OSError, subprocess.CalledProcessError):
        commit = 'local'
    (target / 'build-info.json').write_text(json.dumps({'unoSdk': '6.7.30', 'uno': '6.7.135', 'sample': 'ProPDF.Uno.Sample', 'basePath': '/ProPDF/', 'commit': commit, 'licenses': 'licenses/inventory.json'}), encoding='utf-8')
    print('Staged Uno application, docs and complete notices:', target)


if __name__ == '__main__':
    main()
