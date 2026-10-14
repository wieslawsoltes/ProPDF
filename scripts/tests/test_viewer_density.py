import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location('viewer_density', Path(__file__).resolve().parents[1] / 'avalonia_viewer_density.py')
density = importlib.util.module_from_spec(spec)
spec.loader.exec_module(density)


class ViewerDensityTests(unittest.TestCase):
    def fixture(self, dpr=2):
        css = {'width':1440, 'height':980}
        pixels = {key:round(value*dpr) for key,value in css.items()}
        return {'dpr':dpr, 'host':dict(css), 'canvasCss':dict(css), 'devicePixels':dict(pixels), 'backingStore':dict(pixels)}, {'controls':{'PdfViewer':{'width':1440, 'y':49, 'height':931}}}, css

    def test_matching_native_density(self):
        for scale in (1, 1.25, 1.5, 2, 3):
            measurement, state, viewport = self.fixture(scale)
            density.verify_density(measurement, state, scale, viewport)

    def test_rejects_emulated_dpr_with_unscaled_observer(self):
        measurement, state, viewport = self.fixture()
        measurement['devicePixels'] = dict(viewport)
        with self.assertRaisesRegex(AssertionError, 'Native ResizeObserver'):
            density.verify_density(measurement, state, 2, viewport)

    def test_rejects_upscaled_low_resolution_canvas(self):
        measurement, state, viewport = self.fixture()
        measurement['backingStore'] = dict(viewport)
        with self.assertRaisesRegex(AssertionError, 'Canvas backing'):
            density.verify_density(measurement, state, 2, viewport)

    def test_rejects_half_size_logical_viewer(self):
        measurement, state, viewport = self.fixture()
        state['controls']['PdfViewer']['width'] = 720
        with self.assertRaisesRegex(AssertionError, 'Avalonia logical width'):
            density.verify_density(measurement, state, 2, viewport)

    def test_rejects_half_height_logical_viewer(self):
        measurement, state, viewport = self.fixture()
        state['controls']['PdfViewer']['height'] = 441
        with self.assertRaisesRegex(AssertionError, 'Avalonia logical bottom'):
            density.verify_density(measurement, state, 2, viewport)

    def test_rejects_unrequested_scale(self):
        measurement, state, viewport = self.fixture()
        with self.assertRaisesRegex(AssertionError, 'device pixel ratio'):
            density.verify_density(measurement, state, 1, viewport)

    def test_process_scale_and_argument_validation(self):
        self.assertIn('--force-device-scale-factor=2', density.chromium_arguments(2))
        for scale in (float('nan'), float('inf'), 0, -2, 5):
            with self.assertRaises(ValueError):
                density.chromium_arguments(scale)


if __name__ == '__main__':
    unittest.main()
