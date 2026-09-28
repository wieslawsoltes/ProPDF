import assert from 'node:assert/strict';
import { writeFile } from 'node:fs/promises';

// Synthetic PDF with separate narrow punctuation and stems, not a bundled font.
// The advance-width regression alone could not detect a mono face squeezed into Helvetica.
export function standardFontPdf() {
  const stream = 'BT /R 72 Tf 1 0 0 1 20 130 Tm (.) Tj 1 0 0 1 80 130 Tm (I) Tj 1 0 0 1 140 130 Tm (i) Tj ET\n' +
    'BT /B 72 Tf 1 0 0 1 20 30 Tm (.) Tj 1 0 0 1 80 30 Tm (I) Tj 1 0 0 1 140 30 Tm (i) Tj ET';
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
    '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 220] /Resources << /Font << /R 4 0 R /B 5 0 R >> >> /Contents 6 0 R >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>',
    '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica-Bold /Encoding /WinAnsiEncoding >>',
    `<< /Length ${Buffer.byteLength(stream)} >>\nstream\n${stream}\nendstream`];
  let pdf = '%PDF-1.7\n'; const offsets = [0];
  objects.forEach((o, i) => { offsets.push(Buffer.byteLength(pdf)); pdf += `${i + 1} 0 obj\n${o}\nendobj\n`; });
  const xref = Buffer.byteLength(pdf);
  pdf += `xref\n0 ${offsets.length}\n0000000000 65535 f \n`;
  pdf += offsets.slice(1).map(o => `${String(o).padStart(10, '0')} 00000 n \n`).join('');
  pdf += `trailer\n<< /Size ${offsets.length} /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf);
}

export async function inspectStandardFontImage(page, bytes) {
  const result = await page.evaluate(async base64 => {
    const image = new Image(); image.src = 'data:image/png;base64,' + base64; await image.decode();
    const canvas = document.createElement('canvas'); canvas.width = image.width; canvas.height = image.height;
    const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0);
    const pixels = ctx.getImageData(0, 0, image.width, image.height).data;
    function ink(x0, x1, y0, y1) {
      let left = x1, right = -1, top = y1, bottom = -1, area = 0;
      for (let y=y0; y<y1; y++) for (let x=x0; x<x1; x++) {
        const i = (y * image.width + x) * 4;
        if (pixels[i] < 128 && pixels[i+1] < 128 && pixels[i+2] < 128) {
          left=Math.min(left,x);right=Math.max(right,x);top=Math.min(top,y);bottom=Math.max(bottom,y);area++;
        }
      }
      return { width:right-left+1, height:bottom-top+1, area };
    }
    return { width:image.width, height:image.height,
      regular: { dot:ink(0,65,0,110), stem:ink(70,120,0,110) },
      bold: { dot:ink(0,65,110,220), stem:ink(70,120,110,220) } };
  }, bytes.toString('base64'));
  assert.equal(result.width,300);assert.equal(result.height,220);
  for (const {dot} of [result.regular,result.bold]) {
    assert.ok(dot.height>=5 && dot.width/dot.height>.7 && dot.width/dot.height<1.4,
      `Period is distorted by unsuitable fallback metrics: ${JSON.stringify(dot)}`);
  }
  assert.ok(result.bold.stem.area>result.regular.stem.area*1.25,
    `Bold must actually use a bold outline: ${JSON.stringify(result)}`);
  return result;
}

export async function verifyDensityTransition(browser, base, out) {
  const context = await browser.newContext({ viewport:{width:1200,height:900}, deviceScaleFactor:2 });
  const page=await context.newPage();const errors=[], missing=[];
  page.on('pageerror', e=>errors.push(e.message));
  page.on('response', r=>{if(r.status()>=400 && /\.(wasm|js|dll|ttf|woff2?)(\?|$)/i.test(r.url())) missing.push(r.url());});
  try {
    await page.goto(base+'?test=1',{waitUntil:'domcontentloaded'});
    await page.waitForFunction(()=>globalThis.propdfTest || document.documentElement.dataset.propdfError,null,{timeout:240000});
    assert.equal(await page.evaluate(()=>document.documentElement.dataset.propdfError),undefined);
    async function settled(density) {
      await page.waitForFunction(d=>{
        const s=JSON.parse(propdfTest.State()),t=JSON.parse(propdfTest.Thumbnails());
        return Math.abs(s.density-d)<.01 && s.tiles>0 && !s.rendering && t.some(t=>t.page===1 && t.pixels>0);
      },density,{timeout:30000});
      return page.evaluate(()=>({state:JSON.parse(propdfTest.State()),thumbnails:JSON.parse(propdfTest.Thumbnails())}));
    }
    const retina=await settled(2);assert.equal(retina.state.error,null);
    await page.locator('.uno-loader').waitFor({state:'hidden',timeout:15000});
    const shot=await page.screenshot({path:`${out}/uno-density-2.png`,timeout:15000});
    assert.equal(shot.readUInt32BE(16),2400); assert.equal(shot.readUInt32BE(20),1800);
    // Change ONLY display density, preserving CSS viewport. This catches adapters
    // that update only on a size event and keep blurry cached images on monitor moves.
    const cdp=await context.newCDPSession(page);
    await cdp.send('Emulation.setDeviceMetricsOverride',{width:1200,height:900,deviceScaleFactor:1.25,mobile:false});
    const fractional=await settled(1.25);assert.equal(fractional.state.error,null);
    assert.equal(retina.state.revision,fractional.state.revision);assert.equal(retina.state.zoom,fractional.state.zoom);
    assert.equal(retina.state.viewportWidth,fractional.state.viewportWidth);
    assert.ok(retina.state.rasterPixels>fractional.state.rasterPixels*1.7);
    const a=retina.thumbnails.find(t=>t.page===1), b=fractional.thumbnails.find(t=>t.page===1);
    assert.ok(a.pixels>b.pixels*2.4 && a.pixels<b.pixels*2.7,'Thumbnail pixels must follow display density squared.');
    const final=await page.screenshot({path:`${out}/uno-density-1_25.png`,timeout:15000});
    assert.equal(final.readUInt32BE(16),1500);assert.equal(final.readUInt32BE(20),1125);
    assert.deepEqual(errors,[]);assert.deepEqual(missing,[]);
    await writeFile(`${out}/density.json`,JSON.stringify({retina,fractional,errors,missing},null,2));
  } catch(error) {
    await page.screenshot({path:`${out}/density-failure.png`,timeout:5000}).catch(()=>{});
    await writeFile(`${out}/density-failure.json`,JSON.stringify({error:String(error),errors,missing,state:await page.evaluate(()=>globalThis.propdfTest?JSON.parse(propdfTest.State()):null).catch(()=>null)},null,2));
    throw error;
  } finally { await context.close(); }
}
