#!/usr/bin/env python3
"""Collect textual license evidence, without approving or suppressing failures."""
from __future__ import annotations
import hashlib
import importlib.util
import json
import pathlib
import shutil
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = pathlib.Path(__file__).resolve().parents[1]
OUTPUT = ROOT / 'artifacts/license-evidence'


def main():
    spec = importlib.util.spec_from_file_location('license_audit', ROOT / 'scripts/audit-licenses.py')
    audit = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(audit)
    policy = json.loads((ROOT / 'scripts/license-policy.json').read_text(encoding='utf-8'))
    OUTPUT.mkdir(parents=True, exist_ok=True)
    records = {}
    for asset in ROOT.rglob('project.assets.json'):
        if 'obj' not in asset.parts or 'artifacts' in asset.parts:
            continue
        graph = json.loads(asset.read_text(encoding='utf-8'))
        for key, value in graph.get('libraries', {}).items():
            if value.get('type') != 'package' or key in records:
                continue
            relative = pathlib.PurePosixPath(value['path'])
            if relative.is_absolute() or '..' in relative.parts:
                raise ValueError('Unsafe package path')
            directory = next((pathlib.Path(folder) / relative for folder in graph['packageFolders']
                              if (pathlib.Path(folder) / relative).is_dir()), None)
            if directory is None:
                continue
            record = {'package': key}
            try:
                record.update(audit.inspect_package(key, directory, policy))
            except (ValueError, OSError, ET.ParseError) as error:
                record['error'] = str(error)
                print(str(error))
            record['evidence'] = []
            for candidate in directory.rglob('*'):
                name = candidate.name.lower()
                if not candidate.is_file() or candidate.is_symlink() or candidate.stat().st_size > 2 * 1024 * 1024:
                    continue
                if not (name.endswith('.nuspec') or 'license' in name or 'notice' in name or name.startswith('copyright')):
                    continue
                raw = candidate.read_bytes()
                try:
                    raw.decode('utf-8-sig')
                except UnicodeError:
                    continue
                destination = OUTPUT / relative / candidate.relative_to(directory)
                destination.parent.mkdir(parents=True, exist_ok=True)
                shutil.copyfile(candidate, destination)
                record['evidence'].append({'path': str(destination.relative_to(OUTPUT)), 'sha256': hashlib.sha256(raw).hexdigest()})
            records[key] = record
    (OUTPUT / 'inventory.json').write_text(json.dumps(list(records.values()), indent=2) + '\n', encoding='utf-8')
    # Preparation is not license approval. audit-licenses.py remains a mandatory separate gate.
    subprocess.run([sys.executable, str(ROOT / 'scripts/fetch-uno-notices.py')], check=True)
    print(f'Collected evidence for {len(records)} packages; the strict audit still decides success.')


if __name__ == '__main__':
    main()
