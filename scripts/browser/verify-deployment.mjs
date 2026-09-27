import assert from 'node:assert/strict';
import { mkdir, writeFile } from 'node:fs/promises';
import { spawn } from 'node:child_process';
import { pathToFileURL } from 'node:url';

/** Verify deployment identity before exercising the actual public browser app. */
export async function verifyDeployment(base, commit, {
  request = fetch, attempts = 12, wait = ms => new Promise(resolve => setTimeout(resolve, ms))
} = {}) {
  const root = new URL(base);
  assert.ok(root.protocol === 'https:' || root.protocol === 'http:', 'An HTTP(S) site is required.');
  assert.equal(root.username + root.password, '', 'The site URL must not contain credentials.');
  assert.match(commit, /^[a-f0-9]{40}$/, 'Expected an exact deployment commit.');
  assert.ok(Number.isInteger(attempts) && attempts > 0 && attempts <= 30);
  let info, lastError;
  for (let attempt = 0; attempt < attempts; attempt++) {
    try {
      const url = new URL('build-info.json', root);
      url.searchParams.set('verify', `${commit}-${attempt}-${Date.now()}`);
      const response = await request(url, { headers: { 'Cache-Control': 'no-cache' }, signal: AbortSignal.timeout(10000) });
      assert.equal(response.status, 200, 'Build identity must be publicly available.');
      info = await response.json();
      assert.equal(info.commit, commit, 'The public site must serve this exact commit, not a previous deployment.');
      assert.equal(info.sample, 'ProPDF.Uno.Sample');
      assert.equal(info.basePath, root.pathname);
      lastError = null; break;
    } catch (error) { lastError = error; if (attempt + 1 < attempts) await wait(5000); }
  }
  if (lastError) throw lastError;
  for (const path of ['docs/', 'licenses/inventory.json']) {
    const response = await request(new URL(path, root), { signal: AbortSignal.timeout(10000) });
    assert.equal(response.status, 200, `${path} must be published beside the application.`);
    if (path === 'docs/') {
      const html = await response.text(); assert.match(html, /ProPDF/); assert.match(html, /<html/i);
    } else {
      const inventory = await response.json();
      assert.equal(inventory.policy, 'permissive-only'); assert.equal(inventory.partial, false);
      assert.ok(Array.isArray(inventory.packages) && inventory.packages.length > 0);
    }
  }
  return info;
}

async function main() {
  const base = process.env.PROPDF_URL;
  const commit = process.env.PROPDF_EXPECT_COMMIT;
  assert.ok(base && commit, 'Set PROPDF_URL and PROPDF_EXPECT_COMMIT.');
  const directory = process.env.PROPDF_QA || 'artifacts/browser-live';
  await mkdir(directory, { recursive: true });
  try {
    const info = await verifyDeployment(base, commit);
    await writeFile(`${directory}/deployment.json`, JSON.stringify({ base, expectedCommit: commit, info }, null, 2));
    // Reuse all pointer, editing, download, reopen and independent pixel tests.
    // Only the sample's generated public document is used, never user data.
    const code = await new Promise((resolve, reject) => {
      const child = spawn(process.execPath, ['scripts/browser/uno-smoke.mjs'], {
        stdio: 'inherit', env: { ...process.env, PROPDF_URL: base, PROPDF_QA: directory }
      });
      child.once('error', reject); child.once('exit', (value, signal) => signal ? reject(new Error(`Browser test terminated: ${signal}`)) : resolve(value));
    });
    assert.equal(code, 0, 'The live editor must pass the full browser workflow suite.');
  } catch (error) {
    await writeFile(`${directory}/deployment-failure.json`, JSON.stringify({ base, commit, error: String(error) }, null, 2));
    throw error;
  }
}
if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) await main();
