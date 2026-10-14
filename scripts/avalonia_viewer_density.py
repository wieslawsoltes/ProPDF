"""Fail-closed density evidence for the actual Avalonia browser canvas.

A context-only DPR override can leave ResizeObserver device pixels at scale 1:
https://github.com/microsoft/playwright/issues/18591
Use Chromium's process-level display scale too, then verify native measurements.
Never replace ResizeObserver, devicePixelRatio, the canvas, or application state.
"""
import json
import math


def chromium_arguments(dpr):
    if not math.isfinite(dpr) or not .5 <= dpr <= 4:
        raise ValueError('Browser validation DPR must be finite and between 0.5 and 4.')
    return ['--enable-webgl', '--use-angle=swiftshader', '--enable-unsafe-swiftshader',
            f'--force-device-scale-factor={dpr:g}']


def verify_density(measurement, state, expected_dpr, viewport):
    """Reject inconsistent emulation and upscaled low-resolution canvas output."""
    def close(actual, expected, label, tolerance=1):
        if not isinstance(actual, (int, float)) or not math.isfinite(actual) or abs(actual - expected) > tolerance:
            raise AssertionError(f'{label}: expected {expected}, got {actual}; density={json.dumps(measurement)}')
    close(measurement['dpr'], expected_dpr, 'Browser device pixel ratio', .001)
    for axis in ('width', 'height'):
        close(measurement['host'][axis], viewport[axis], 'Host CSS ' + axis)
        close(measurement['canvasCss'][axis], viewport[axis], 'Canvas CSS ' + axis)
        close(measurement['devicePixels'][axis], round(viewport[axis] * expected_dpr), 'Native ResizeObserver pixels ' + axis)
        close(measurement['backingStore'][axis], round(viewport[axis] * expected_dpr), 'Canvas backing pixels ' + axis)
    viewer = state['controls']['PdfViewer']
    close(viewer['width'], viewport['width'], 'Avalonia logical width')
    close(viewer['y'] + viewer['height'], viewport['height'], 'Avalonia logical bottom')


async def capture_density(page):
    return await page.evaluate('''() => new Promise((resolve, reject) => {
        const host = document.querySelector('#out');
        const canvas = host?.querySelector('canvas');
        if (!canvas) { reject(new Error('Avalonia canvas is missing.')); return; }
        const size = r => ({width:r.width, height:r.height});
        let timer;
        const observer = new ResizeObserver(entries => {
            const entry = entries.find(e => e.target === canvas);
            if (!entry) return;
            observer.disconnect(); clearTimeout(timer);
            const box = entry.devicePixelContentBoxSize?.[0];
            if (!box) { reject(new Error('Native device-pixel resize measurements are unavailable.')); return; }
            resolve({ dpr:window.devicePixelRatio,
                host:size(host.getBoundingClientRect()), canvasCss:size(canvas.getBoundingClientRect()),
                devicePixels:{width:box.inlineSize, height:box.blockSize},
                backingStore:{width:canvas.width, height:canvas.height} });
        });
        timer = setTimeout(() => { observer.disconnect(); reject(new Error('Native resize observation timed out.')); }, 5000);
        observer.observe(canvas, {box:'content-box'});
    })''')
