import assert from 'node:assert/strict';
import test from 'node:test';
import { verifyDeployment } from './verify-deployment.mjs';
const commit = 'a'.repeat(40);
const base = 'https://example.test/ProPDF/';
const good = { commit, sample: 'ProPDF.Uno.Sample', basePath: '/ProPDF/' };
function responses(identity = good, inventory = { policy: 'permissive-only', partial: false, packages: [{}] }) {
  return async url => ({ status: 200, json: async () => url.pathname.endsWith('build-info.json') ? identity : inventory, text: async () => '<html>ProPDF documentation</html>' });
}
test('verifies exact commit and both published companion resources', async () => {
  const calls = []; const request = responses();
  assert.equal(await verifyDeployment(base, commit, { request: async (...args) => { calls.push(String(args[0])); return request(...args); } }), good);
  assert.equal(calls.length, 3); assert.ok(calls[0].includes(commit));
});
test('stale CDN identity retries, then validates the new exact deployment', async () => {
  let attempts = 0, waits = 0; const request = responses();
  await verifyDeployment(base, commit, { request: (...args) => {
    if (args[0].pathname.endsWith('build-info.json') && attempts++ === 0) return responses({ ...good, commit: 'b'.repeat(40) })(...args);
    return request(...args);
  }, wait: async () => { waits++; } });
  assert.equal(attempts, 2); assert.equal(waits, 1);
});
test('old deployment never passes after the bounded retries', async () => {
  let waits = 0;
  await assert.rejects(verifyDeployment(base, commit, { attempts: 2, request: responses({ ...good, commit: 'b'.repeat(40) }), wait: async () => { waits++; } }), /previous deployment/);
  assert.equal(waits, 1);
});
test('missing published docs is a deployment failure', async () => {
  const request = responses();
  await assert.rejects(verifyDeployment(base, commit, { request: (...args) => args[0].pathname.endsWith('docs/') ? { status: 404 } : request(...args) }), /published beside/);
});
test('a partial license inventory cannot qualify the public site', async () => {
  await assert.rejects(verifyDeployment(base, commit, { request: responses(good, { policy: 'permissive-only', partial: true, packages: [{}] }) }));
});
test('wrong application base path is rejected', async () => {
  await assert.rejects(verifyDeployment(base, commit, { attempts: 1, request: responses({ ...good, basePath: '/' }) }));
});
test('credentials and non-commit expectations are rejected without requests', async () => {
  const request = () => { throw new Error('Should not fetch'); };
  await assert.rejects(verifyDeployment('https://user:password@example.test/ProPDF/', commit, { request }), /credentials/);
  await assert.rejects(verifyDeployment(base, 'main', { request }), /exact deployment commit/);
});
