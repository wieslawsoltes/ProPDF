#!/usr/bin/env python3
"""Retain exact reviewed public license texts omitted by legacy Uno packages.

Only the three legal documents below are downloaded; no code, fonts or secrets.
Every response is bounded and checked against its reviewed Git blob identity.
Run before packing or publishing the Uno sample. Cached verified files work offline.
"""
from __future__ import annotations
import hashlib
import pathlib
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parents[1]
FILES = (
    ('Uno-Eventing-LICENSE.txt', 'https://raw.githubusercontent.com/unoplatform/Uno.Diagnostics.Eventing/main/License.md', 'b48e38bd66fc14eb89ad5d050c688f721c1aa6f0'),
    ('Uno-ICU-LICENSE.txt', 'https://raw.githubusercontent.com/unoplatform/uno.icu/main/LICENSE.md', '11da17f1888fee788ec381a13585318e171f7d86'),
    ('ICU-77-LICENSE.txt', 'https://raw.githubusercontent.com/unicode-org/icu/release-77-1/LICENSE', '0b9efcd9092f977797c2572de717ca2edac50bc2'),
)


def verify(raw: bytes, expected: str):
    if len(raw) > 128 * 1024:
        raise ValueError('License document exceeds its size bound')
    raw.decode('utf-8-sig')
    identity = hashlib.sha1(b'blob ' + str(len(raw)).encode('ascii') + b'\0' + raw).hexdigest()
    if identity != expected:
        raise ValueError('Upstream license changed; explicit review is required, not automatic acceptance')


def main():
    directory = ROOT / 'artifacts/upstream-notices'
    directory.mkdir(parents=True, exist_ok=True)
    for name, url, identity in FILES:
        output = directory / name
        if output.exists():
            raw = output.read_bytes()
        else:
            request = urllib.request.Request(url, headers={'User-Agent': 'ProPDF-license-provenance'})
            with urllib.request.urlopen(request, timeout=30) as response:
                raw = response.read(128 * 1024 + 1)
        verify(raw, identity)
        output.write_bytes(raw)
    print('PASS: three complete, source-pinned Uno/ICU license notices retained.')


if __name__ == '__main__':
    main()
