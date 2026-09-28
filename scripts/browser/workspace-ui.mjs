import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';

// Pointer/keyboard interactions against the actual shared native control tree.
// Never assigns layout, ItemsSource, document bytes, or a mock DOM representation.
export async function verifyWorkspaceChrome(page, out, state, pointer) {
  const revision = (await state()).revision;
  const layouts = [];
  for (const width of [1450, 800, 390]) {
    await page.setViewportSize({width, height:900});
    await pointer('AllToolsButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).toolsOpen);
    await pointer('ToolEditButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).section === 'Edit');
    // Draft text survives task changes: existing panels are re-used, not rebuilt.
    await page.evaluate(() => propdfTest.Text('ToolTextInput','Persistent tool draft'));
    await pointer('CommentsPaneButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).section === 'Review');
    await pointer('PagesPaneButton');
    await page.waitForFunction(() => JSON.parse(propdfTest.State()).pagesOpen);
    const opened = await state();
    assert.equal(opened.toolDraft,'Persistent tool draft','Task changes must retain draft input.');
    if (width < 980) assert.equal(opened.toolsOpen,false,'Narrow drawers must be exclusive.');
    const controls = await page.evaluate(() => JSON.parse(propdfTest.Controls()));
    const pages = controls.find(c=>c.name==='PagesPane' && c.visible);
    assert.ok(pages && pages.x >= 0 && pages.x+pages.width <= width,'Drawer must stay inside the viewport.');
    await pointer('ClosePagesButton');
    if ((await state()).toolsOpen) await pointer('CloseToolsButton');
    await page.waitForFunction(() => !JSON.parse(propdfTest.State()).pagesOpen && !JSON.parse(propdfTest.State()).toolsOpen);
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
    assert.ok(closed.viewportWidth>=width-50,'Compact drawers must overlay, not shrink the PDF permanently.');
    layouts.push({width,opened,closed});
  }
  await page.setViewportSize({width:1450,height:960});
  await pointer('AllToolsButton'); await pointer('FitPageButton');
  await page.waitForFunction(() => { const s=JSON.parse(propdfTest.State()); return !s.rendering && s.tiles>0; });
  await page.screenshot({path:`${out}/workspace-all-tools.png`});
  await writeFile(`${out}/workspace-ui.json`,JSON.stringify({revision,layouts},null,2));
}
