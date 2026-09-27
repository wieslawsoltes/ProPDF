import hashlib
import importlib.util
import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[2]


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, ROOT / path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


audit = load('uno_license_audit', 'scripts/audit-licenses.py')
notices = load('uno_notices', 'scripts/fetch-uno-notices.py')


class UnoLicensePolicyTests(unittest.TestCase):
    def test_unicode_does_not_admit_restricted_licenses(self):
        self.assertTrue(audit.permissive_expression('Apache-2.0 AND Unicode-3.0'))
        for license in ['LGPL-2.1-or-later', 'MIT OR LGPL-2.1-or-later', 'LicenseRef-Microsoft-SDK', 'AGPL-3.0-only']:
            self.assertFalse(audit.permissive_expression(license))
        with self.assertRaises(ValueError):
            audit.inspect_package('LibVLCSharp/3.9.7.1', pathlib.Path('missing'), {})

    def test_notice_identity_is_fail_closed(self):
        raw = b'Example legal text\n'
        identity = hashlib.sha1(b'blob ' + str(len(raw)).encode() + b'\0' + raw).hexdigest()
        notices.verify(raw, identity)
        with self.assertRaises(ValueError):
            notices.verify(raw + b'changed', identity)
        with self.assertRaises(ValueError):
            notices.verify(b'x' * (128 * 1024 + 1), identity)
