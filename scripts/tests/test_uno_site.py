import importlib.util
import pathlib
import tempfile
import unittest

SPEC = importlib.util.spec_from_file_location('uno_site', pathlib.Path(__file__).parents[1] / 'stage-uno-site.py')
site = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(site)


class UnoSiteTests(unittest.TestCase):
    def fixture(self, directory, hashed=True):
        directory.mkdir(parents=True)
        (directory / 'index.html').write_text('<html></html>')
        (directory / '_framework').mkdir()
        scripts = directory / 'package_012345' if hashed else directory
        scripts.mkdir(exist_ok=True)
        (scripts / 'uno-bootstrap.js').write_text('// bootstrap')
        return directory

    def test_hashed_uno_distribution_not_documentation(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            expected = self.fixture(root / 'wwwroot')
            (root / 'docs').mkdir()
            (root / 'docs/index.html').write_text('documentation')
            self.assertEqual(expected, site.find_distribution(root))

    def test_flat_layout_and_ambiguous_outputs(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            expected = self.fixture(root / 'flat', False)
            self.assertEqual(expected, site.find_distribution(root))
            self.fixture(root / 'second')
            with self.assertRaises(ValueError):
                site.find_distribution(root)

    def test_missing_runtime_is_not_a_published_application(self):
        with tempfile.TemporaryDirectory() as temp:
            root = pathlib.Path(temp)
            (root / 'index.html').write_text('not a built app')
            (root / 'uno-bootstrap.js').write_text('// incomplete')
            with self.assertRaises(ValueError):
                site.find_distribution(root)
