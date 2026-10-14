"""Exercise the real Avalonia canvas using browser input, not a JavaScript replacement UI."""
import argparse
import asyncio
import json
import time
import traceback
from pathlib import Path
from PIL import Image
from playwright.async_api import async_playwright

async def run(args):
    output = Path(args.output)
    output.mkdir(parents=True, exist_ok=True)
    checks, console, errors = [], [], []
    started = time.monotonic()
    async with async_playwright() as playwright:
        browser = await playwright.chromium.launch(headless=True, args=['--enable-webgl', '--use-angle=swiftshader', '--enable-unsafe-swiftshader'])
        context = await browser.new_context(viewport={'width': 1440, 'height': 980}, device_scale_factor=args.dpr,
                                            accept_downloads=True, permissions=['clipboard-read', 'clipboard-write'])
        page = await context.new_page()
        page.set_default_timeout(20000)
        page.on('console', lambda message: console.append({'type': message.type, 'text': message.text}))
        page.on('pageerror', lambda error: errors.append(str(error)))
        page.on('dialog', lambda dialog: dialog.accept(dialog.default_value) if dialog.type == 'prompt' else dialog.accept())
        async def state():
            return await page.evaluate('globalThis.viewerProbe ? viewerProbe() : ({ready:false})')
        async def until(predicate, label, timeout=25):
            deadline = time.monotonic() + timeout
            last = {}
            while time.monotonic() < deadline:
                last = await state()
                if last.get('startupError'):
                    raise AssertionError('Browser startup failed: ' + last['startupError'])
                if predicate(last):
                    return last
                await asyncio.sleep(.12)
            raise AssertionError(f'Timed out: {label}; state={json.dumps(last, ensure_ascii=False)}')
        def passed(name, details=None):
            checks.append({'name': name, 'passed': True, 'details': details})
            print('PASS ' + name, flush=True)
        async def click(name, enabled=True):
            s = await until(lambda s: name in s.get('controls', {}), 'visible control ' + name)
            control = s['controls'][name]
            if enabled and not control['enabled']:
                raise AssertionError('Control is unexpectedly disabled: ' + name)
            x, y = control['x'] + control['width'] / 2, control['y'] + control['height'] / 2
            size = page.viewport_size
            if x < 0 or y < 0 or x >= size['width'] or y >= size['height']:
                raise AssertionError(f'Control is outside viewport: {name}: {control}')
            await page.mouse.click(x, y)
            await asyncio.sleep(.12)
        async def text(name, value, enter=False):
            await click(name)
            await page.keyboard.press('Control+A')
            await page.keyboard.press('Backspace')
            if value:
                await page.keyboard.type(value, delay=12)
            if enter:
                await page.keyboard.press('Enter')
            await asyncio.sleep(.12)
        async def select(name, index):
            await click(name)
            await page.keyboard.press('Home')
            for _ in range(index):
                await page.keyboard.press('ArrowDown')
            await page.keyboard.press('Enter')
            await asyncio.sleep(.2)
        async def settle():
            return await until(lambda s: s.get('ready') and not s.get('busy') and not s.get('rendering') and not s.get('searching'), 'settled viewer')
        async def download(name, destination):
            async with page.expect_download() as result:
                await click(name)
            item = await result.value
            await item.save_as(str(destination))
            await settle()
            return destination.read_bytes()
        async def document_drag(x1, y1, x2, y2):
            s = await settle()
            surface = s['controls']['PART_Surface']
            bounds = s['pageBounds']
            scale = s['zoom'] * 96 / 72
            def point(x, y):
                return surface['x'] + bounds['X'] + x * scale, surface['y'] + bounds['Y'] + y * scale
            start, end = point(x1, y1), point(x2, y2)
            await page.mouse.move(*start)
            await page.mouse.down()
            await page.mouse.move(*end, steps=18)
            await page.mouse.up()
            await asyncio.sleep(.2)
        try:
            await page.goto(args.url, wait_until='networkidle', timeout=120000)
            s = await until(lambda s: s.get('ready') and s.get('pages') == 3 and s.get('sceneTileCount', 0) > 0 and s.get('rasterPixels', 0) > 0, 'real PDF raster boot', timeout=90)
            assert s['fontFaceCount'] > 0 and not s['error'], s
            assert s['readOnly'] and not s['controls']['HighlightButton']['enabled'], s
            passed('PdfPig document opens in the actual Avalonia WebAssembly app', {'pages': s['pages'], 'tiles': s['sceneTileCount'], 'rasterPixels': s['rasterPixels'], 'fontFaces': s['fontFaceCount']})
            passed('Read-only is the default and editing tools are disabled')
            await click('FitPageButton')
            await settle()
            await page.screenshot(path=str(output / 'viewer-light.png'))
            image = Image.open(output / 'viewer-light.png').convert('RGB')
            navy = sum(1 for r, g, b in image.getdata() if 15 <= r <= 40 and 30 <= g <= 65 and 55 <= b <= 105)
            assert navy > 5000 * args.dpr * args.dpr, f'Missing rendered PDF title card ({navy} pixels)'
            passed('Screenshot contains the rendered PDF title card, not an empty canvas', {'navyPixels': navy})
            await click('NextPageButton')
            await until(lambda s: s.get('page') == 2, 'next page')
            await click('LastPageButton')
            await until(lambda s: s.get('page') == 3, 'last page')
            await click('FirstPageButton')
            await until(lambda s: s.get('page') == 1, 'first page')
            passed('First, next and last page buttons navigate')
            await text('PageInput', '2', True)
            await until(lambda s: s.get('page') == 2, 'page input')
            passed('Editable page input navigates on Enter')
            await text('ZoomInput', '125%', True)
            await until(lambda s: abs(s.get('zoom', 0) - 1.25) < .001 and s.get('zoomMode') == 'Custom', 'custom zoom')
            passed('Zoom input applies 125 percent')
            await click('ZoomInButton')
            await until(lambda s: s.get('zoom', 0) > 1.25, 'zoom in')
            await click('ZoomOutButton')
            await until(lambda s: abs(s.get('zoom', 0) - 1.25) < .001, 'zoom out')
            passed('Zoom in and out preserve proportional steps')
            for index, mode in [(2, 'SinglePage'), (1, 'Facing'), (0, 'Continuous')]:
                await select('LayoutSelect', index)
                await until(lambda s: s.get('layout') == mode, mode + ' layout')
                await click('FitWidthButton')
                await settle()
                passed(mode + ' layout and fit width')
            await page.keyboard.press('Control+f')
            await text('PART_SearchBox', 'PDF', True)
            await until(lambda s: not s.get('searching') and s.get('search') == 'PDF' and s.get('hits', 0) >= 3, 'PDF search')
            passed('Keyboard find opens a live document-wide search')
            s = await state()
            old = s['hitIndex']
            await click('NextMatchButton')
            await until(lambda s: s.get('hitIndex') != old, 'next match')
            await click('PreviousMatchButton')
            await until(lambda s: s.get('hitIndex') == old, 'previous match')
            passed('Next and previous search result navigation')
            await click('MatchCaseCheck')
            await text('PART_SearchBox', 'pDf', True)
            await until(lambda s: not s.get('searching') and s.get('search') == 'pDf' and s.get('hits') == 0, 'case-sensitive search')
            passed('Case-sensitive search distinguishes text case')
            await click('MatchCaseCheck')
            await click('RegexCheck')
            await text('PART_SearchBox', '[', True)
            await until(lambda s: bool(s.get('error')) and not s.get('searching'), 'regex validation')
            passed('Invalid regular expressions produce a visible error without crashing')
            await click('RegexCheck')
            await text('PART_SearchBox', '', True)
            await click('DismissErrorButton')
            await until(lambda s: not s.get('error') and not s.get('searching') and s.get('hits') == 0, 'search clear')
            await click('CloseSearchButton')
            passed('Search and error state can be cleared')
            await select('SidebarSelect', 1)
            await until(lambda s: s.get('bookmarkCount') == 3 and 'BookmarkItem_Architecture' in s.get('controls', {}), 'outline')
            await click('BookmarkItem_Architecture')
            await until(lambda s: s.get('page') == 3, 'bookmark destination')
            passed('Outline selection follows an actual PDF bookmark')
            await click('BackButton')
            await until(lambda s: s.get('page') != 3, 'navigation history')
            passed('Previous-view history restores the previous destination')
            await select('SidebarSelect', 0)
            await click('PageItem1')
            await until(lambda s: s.get('page') == 1, 'thumbnail navigation')
            passed('Virtualized thumbnail item navigates to its page')
            await click('FitPageButton')
            await click('SelectTextButton')
            revision = (await state())['revision']
            await document_drag(35, 240, 560, 330)
            s = await until(lambda s: 'shared engine' in s.get('selectedText', '').lower(), 'text selection')
            assert s['revision'] == revision
            passed('Pointer selection extracts real PDF text without editing the document')
            await page.keyboard.press('Control+c')
            clipboard = await page.evaluate('navigator.clipboard.readText()')
            assert 'shared engine' in clipboard.lower(), clipboard
            passed('Selected PDF text reaches the browser clipboard')
            await click('EnableEditingCheck')
            await until(lambda s: not s.get('readOnly') and s['controls']['NoteButton']['enabled'], 'editing enabled')
            passed('Only the host editing option enables editing tools')
            await click('NoteButton')
            old_count = (await state())['annotationCount']
            await document_drag(410, 480, 455, 515)
            await until(lambda s: s.get('annotationCount', 0) == old_count + 1 and s.get('dirty'), 'note annotation')
            passed('Dragging the note tool creates a PDF annotation and dirty revision')
            await click('UndoButton')
            await until(lambda s: s.get('annotationCount') == old_count, 'undo note')
            await click('RedoButton')
            await until(lambda s: s.get('annotationCount') == old_count + 1, 'redo note')
            await click('UndoButton')
            await until(lambda s: s.get('annotationCount') == old_count, 'restore note state')
            passed('Undo and redo apply real annotation revisions')
            await select('SidebarSelect', 4)
            await click('FieldItem_Name')
            await text('FieldValueInput', 'Browser viewer review')
            await click('ApplyFieldButton')
            await until(lambda s: any(f['Name'] == 'Name' and f['Value'] == 'Browser viewer review' for f in (s.get('formValues') or [])), 'form field edit')
            passed('Forms panel changes the PDF field value')
            saved = output / 'edited-document.pdf'
            data = await download('SaveButton', saved)
            assert data.startswith(b'%PDF-') and len(data) > 2000
            await until(lambda s: not s.get('dirty'), 'successful user save')
            passed('Save copy downloads a real PDF and marks clean only after output succeeds', {'bytes': len(data)})
            await select('SidebarSelect', 7)
            await text('ExportDpiInput', '72')
            image_data = await download('ExportPngButton', output / 'exported-page.png')
            assert image_data.startswith(b'\x89PNG\r\n\x1a\n')
            passed('PNG export produces an actual image download', {'bytes': len(image_data)})
            await text('PageRangeInput', 'all')
            text_data = await download('ExportTextButton', output / 'exported-text.txt')
            assert 'ProPDF' in text_data.decode('utf-8-sig')
            passed('Text export extracts content from the PDF document')
            old_revision = (await state())['revision']
            async with page.expect_file_chooser() as chooser:
                await click('OpenButton')
            await (await chooser.value).set_files(str(saved))
            await until(lambda s: s.get('pages') == 3 and s.get('revision') != old_revision and not s.get('busy'), 'reopen downloaded PDF')
            await until(lambda s: any(f['Name'] == 'Name' and f['Value'] == 'Browser viewer review' for f in (s.get('formValues') or [])), 'saved form round trip')
            passed('Browser file picker reopens the saved PDF with its edited form value')
            old_revision = (await state())['revision']
            async with page.expect_file_chooser() as chooser:
                await click('OpenButton')
            await (await chooser.value).set_files({'name': 'malformed.pdf', 'mimeType': 'application/pdf', 'buffer': b'This is not a PDF.'})
            s = await until(lambda s: bool(s.get('error')) and not s.get('busy'), 'invalid PDF error')
            assert s['revision'] == old_revision and s['pages'] == 3
            passed('Malformed PDF input preserves the previously open document')
            await click('DismissErrorButton')
            await select('SidebarSelect', 0)
            await click('FirstPageButton') if (await state())['page'] != 1 else asyncio.sleep(0)
            await click('FitPageButton')
            await click('DarkThemeCheck')
            await until(lambda s: s.get('theme') == 'Dark', 'dark theme')
            await page.screenshot(path=str(output / 'viewer-dark.png'))
            passed('Dark theme updates the viewer and sample host')
            await click('MinimalTemplateCheck')
            await until(lambda s: 'MinimalNextButton' in s.get('controls', {}) and 'NextPageButton' not in s.get('controls', {}), 'minimal template')
            await click('MinimalNextButton')
            await until(lambda s: s.get('page') == 2, 'retemplated navigation')
            passed('Replacing the whole viewer template preserves navigation and rendering')
            await click('MinimalTemplateCheck')
            await until(lambda s: 'NextPageButton' in s.get('controls', {}) and 'MinimalNextButton' not in s.get('controls', {}), 'restore default template')
            await click('NextPageButton')
            await until(lambda s: s.get('page') == 3, 'default navigation restored')
            passed('Restoring the default template reconnects its parts correctly')
            await page.set_viewport_size({'width': 650, 'height': 880})
            await until(lambda s: s.get('compact') and s.get('viewportWidth', 0) > 500, 'compact overlay sidebar')
            await click('SidebarButton')
            await until(lambda s: not s.get('sidebar'), 'compact sidebar toggle')
            await click('FitPageButton')
            await settle()
            await page.screenshot(path=str(output / 'viewer-compact.png'))
            passed('Narrow browser view uses an overlay sidebar without collapsing the PDF surface')
            assert not errors, errors
            unexpected = [message['text'] for message in console if message['type'] == 'error']
            assert not unexpected, unexpected
            passed('No uncaught browser errors or console errors')
            report = {'passed': True, 'checks': checks, 'browser': browser.version, 'deviceScaleFactor': args.dpr,
                      'durationSeconds': round(time.monotonic() - started, 2), 'finalState': await state()}
        except Exception as error:
            report = {'passed': False, 'checks': checks, 'error': str(error), 'traceback': traceback.format_exc(), 'deviceScaleFactor': args.dpr}
            try:
                report['finalState'] = await state()
                await page.screenshot(path=str(output / 'failure.png'))
            except Exception as capture_error:
                report['captureError'] = str(capture_error)
            raise
        finally:
            (output / 'report.json').write_text(json.dumps(report, indent=2, ensure_ascii=False))
            (output / 'console.json').write_text(json.dumps(console, indent=2, ensure_ascii=False))
            (output / 'page-errors.json').write_text(json.dumps(errors, indent=2))
            await context.close()
            await browser.close()

if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--url', required=True)
    parser.add_argument('--output', required=True)
    parser.add_argument('--dpr', type=float, default=1)
    asyncio.run(run(parser.parse_args()))
