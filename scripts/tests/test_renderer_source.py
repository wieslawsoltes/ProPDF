import importlib.util
import json
import pathlib
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location("renderer_inventory", pathlib.Path(__file__).parents[1] / "verify-renderer-source.py")
module = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(module)


class RendererSourceTests(unittest.TestCase):
    def fixture(self, directory):
        directory = pathlib.Path(directory)
        files = {"LICENSE.txt": "Apache License\n", "NOTICE.txt": "Upstream notices\n",
                 "Renderer.cs": "// Apache License\n// Modified by ProPDF\n"}
        for name, text in files.items():
            (directory / name).write_bytes(text.encode("utf-8"))
        manifest = {"commit": module.PIN, "repository": module.REPOSITORY, "license": "Apache-2.0", "files": [
            {"path": name, "upstream_sha256": "0" * 64, "vendored_sha256": module.canonical_hash(directory / name)} for name in files]}
        (directory / "PROVENANCE.json").write_text(json.dumps(manifest), encoding="utf-8")
        (directory / "PATCHES.md").write_text("Reviewed modifications", encoding="utf-8")
        return directory

    def test_valid_inventory_and_checkout_line_endings(self):
        with tempfile.TemporaryDirectory() as work:
            root = self.fixture(work)
            self.assertEqual(3, module.verify(root))
            source = root / "Renderer.cs"
            source.write_bytes(source.read_text(encoding="utf-8").replace("\n", "\r\n").encode("utf-8"))
            self.assertEqual(3, module.verify(root))

    def test_doubled_carriage_returns_are_content_changes(self):
        with tempfile.TemporaryDirectory() as work:
            root = self.fixture(work)
            source = root / "Renderer.cs"
            source.write_bytes(source.read_bytes().replace(b"\n", b"\r\r\n"))
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                module.verify(root)

    def test_modified_source_is_rejected(self):
        with tempfile.TemporaryDirectory() as work:
            root = self.fixture(work)
            (root / "Renderer.cs").write_text("changed", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "hash mismatch"):
                module.verify(root)

    def test_extra_file_and_missing_notice_are_rejected(self):
        with tempfile.TemporaryDirectory() as work:
            root = self.fixture(work)
            (root / "extra.cs").write_text("unreviewed", encoding="utf-8")
            with self.assertRaisesRegex(ValueError, "Unreviewed"):
                module.verify(root)
            (root / "extra.cs").unlink()
            (root / "NOTICE.txt").unlink()
            with self.assertRaises(ValueError):
                module.verify(root)

    def test_traversal_and_pin_changes_are_rejected(self):
        with tempfile.TemporaryDirectory() as work:
            root = self.fixture(work)
            path = root / "PROVENANCE.json"
            data = json.loads(path.read_text())
            data["files"][0]["path"] = "../LICENSE.txt"
            path.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, "Unsafe"):
                module.verify(root)
            data["commit"] = "changed"
            path.write_text(json.dumps(data))
            with self.assertRaisesRegex(ValueError, "provenance"):
                module.verify(root)
