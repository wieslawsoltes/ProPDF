import importlib.util
import pathlib
import tempfile
import unittest

MODULE = pathlib.Path(__file__).resolve().parents[1] / "audit-licenses.py"
spec = importlib.util.spec_from_file_location("audit", MODULE)
audit = importlib.util.module_from_spec(spec)
spec.loader.exec_module(audit)


class LicenseAuditTests(unittest.TestCase):
    def test_only_complete_permissive_expressions_are_accepted(self):
        for expression in ["MIT", "Apache-2.0", "(MIT OR Apache-2.0) AND BSD-3-Clause"]:
            self.assertTrue(audit.permissive_expression(expression), expression)
        for expression in ["", "AGPL-3.0-only", "MIT OR AGPL-3.0", "LicenseRef-Commercial", "MIT Apache-2.0", "(MIT", "MIT AND", "MIT;", "MIT WITH unknown"]:
            self.assertFalse(audit.permissive_expression(expression), expression)

    def test_restricted_vendor_packages_are_rejected_even_with_a_misleading_manifest(self):
        for package in ["itext/9.7.0", "itext.pdfsweep/5.0.7", "itext.bouncy-castle-adapter/9.7.0"]:
            with self.assertRaises(ValueError):
                audit.inspect_package(package, pathlib.Path("missing"), {})

    def test_unknown_manifest_fails_and_reviewed_hash_cannot_drift(self):
        with tempfile.TemporaryDirectory() as folder:
            directory = pathlib.Path(folder)
            manifest = directory / "example.nuspec"
            manifest.write_text("<package><metadata><id>example</id></metadata></package>")
            with self.assertRaises(ValueError):
                audit.inspect_package("example/1.0.0", directory, {})
            policy = {"reviewed": {"example/1.0.0": {"license": "MIT", "manifest_sha256": "0" * 64, "source": "review", "note": "test"}}}
            with self.assertRaises(ValueError):
                audit.inspect_package("example/1.0.0", directory, policy)

    def test_empty_repository_does_not_pass(self):
        with tempfile.TemporaryDirectory() as folder:
            root = pathlib.Path(folder)
            policy = root / "policy.json"
            policy.write_text('{"reviewed":{}}')
            with self.assertRaises(ValueError):
                audit.audit(root, policy)


if __name__ == "__main__":
    unittest.main()
