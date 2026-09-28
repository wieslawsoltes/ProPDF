import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';

// Pointer/keyboard interactions against the actual shared native control tree.
// Never assigns layout, ItemsSource, document bytes, or a mock DOM representation.
export async function verifyWorkspaceChrome(page, out, state, pointer) {
  const revision = (await state()).revision;
  const layouts = [];
  for (const width of [1450, 800, 390]) {
    console.log(`Browser workspace layout: ${width} DIP`);
    await page.setViewportSize({width, height:900});
    await pointer('AllToolsButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).toolsOpen);
    await pointer('ToolEditButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).section === 'Edit');
    await page.waitForFunction(() => JSON.parse(propdfTest.Controls()).some(c => c.name === 'ToolTextInput' && c.visible));
    // Draft text survives task changes: existing panels are re-used, not rebuilt.
    await page.evaluate(() => propdfTest.Text('ToolTextInput','Persistent tool draft'));
    await pointer('CommentsPaneButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).section === 'Review');
    await pointer('PagesPaneButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).pagesOpen);
    await page.waitForFunction(width => {
      const s = JSON.parse(propdfTest.State());
      return width >= 980 || Math.abs(s.viewWidth - (width - 48)) <= 1;
    }, width);
    const opened = await state();
    assert.equal(opened.toolDraft,'Persistent tool draft','Task changes must retain draft input.');
    if (width < 980) assert.equal(opened.toolsOpen,false,'Narrow drawers must be exclusive.');
    const controls = await page.evaluate(() => JSON.parse(propdfTest.Controls()));
    const pages = controls.find(c=>c.name==='PagesPane' && c.visible);
    assert.ok(pages && pages.x >= 0 && pages.x+pages.width <= width,'Drawer must stay inside the viewport.');
    await pointer('ClosePagesButton');
    if ((await state()).toolsOpen) await pointer('CloseToolsButton');
    await page.waitForFunction(width => {
      const s = JSON.parse(propdfTest.State());
      return !s.pagesOpen && !s.toolsOpen && Math.abs(s.viewWidth - (width - 48)) <= 1;
    }, width);
    await pointer('QuickPanButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).tool==='Pan');
    await pointer('QuickHighlightButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).tool==='Highlight');
    await pointer('FindPaneButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).searchOpen);
    await pointer('SearchQueryInput');
    await page.keyboard.press('Escape');
    await page.waitForFunction(() => !JSON.parse(propdfTest.State()).searchOpen);
    assert.equal((await state()).revision,revision,'Shell navigation must never edit the PDF.');
    await pointer('QuickPanButton');
    await pointer('FitPageButton');
    await page.waitForFunction(() => { const s=JSON.parse(propdfTest.State()); return !s.rendering && s.tiles>0; });
    await page.screenshot({path:`${out}/workspace-${width}.png`});
    const closed=await state();
    assert.ok(Math.abs(closed.viewWidth - (width - 48)) <= 1,
      'Closed drawers must leave only the 48-DIP rail outside the viewer.');
    // The viewer itself reserves 12 DIP for its native vertical scrollbar.
    // Compare shell layout to the outer view, not to the PDF raster interior.
    assert.ok(Math.abs(closed.viewportWidth - (closed.viewWidth - 12)) <= 1,
      'The document interior must occupy the viewer apart from its scrollbar.');
    if (width < 980) assert.ok(Math.abs(opened.viewWidth - closed.viewWidth) <= 1,
      'Compact drawers must overlay rather than shrink the viewer.');
    layouts.push({width,opened,closed});
  }
  await page.setViewportSize({width:1450,height:960});
  await pointer('AllToolsButton'); await pointer('FitPageButton');
  await page.waitForFunction(() => { const s=JSON.parse(propdfTest.State()); return !s.rendering && s.tiles>0; });
  await page.screenshot({path:`${out}/workspace-all-tools.png`});
  await writeFile(`${out}/workspace-ui.json`,JSON.stringify({revision,layouts},null,2));
}
