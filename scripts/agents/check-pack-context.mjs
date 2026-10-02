import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
fs.mkdirSync(path.join(root, 'artifacts'), { recursive: true });
const fixture = fs.mkdtempSync(path.join(root, 'artifacts/pack-path-check-'));
const outside = fs.mkdtempSync(path.join(os.tmpdir(), 'assetblock-pack-check-'));
const link = path.join(fixture, 'linked');
const run = (scope) => spawnSync(process.execPath, ['scripts/agents/pack-context.mjs', scope], {
  cwd: root, encoding: 'utf8', windowsHide: true,
});
try {
  fs.writeFileSync(path.join(outside, 'sample.txt'), 'Synthetic fixture only.');
  fs.symlinkSync(outside, link, process.platform === 'win32' ? 'junction' : 'dir');
  assert.equal(fs.lstatSync(path.join(link, 'sample.txt')).isSymbolicLink(), false);
  for (const target of [link, path.join(link, 'sample.txt')]) {
    const result = run(path.relative(root, target).replaceAll('\\', '/'));
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /Symlink\/junction paths are unsupported/);
  }
  for (const scope of ['.', '../outside', 'asblock-backend']) {
    const result = run(scope);
    assert.notEqual(result.status, 0);
    assert.match(result.stderr, /explicit repository-relative feature path|narrower feature path/);
  }
  console.log('Passed 5 scope rejection checks, including linked ancestor and junction scope.');
} finally {
  // Remove only this fixture's link and known files; never recurse through a junction.
  if (fs.existsSync(link)) fs.unlinkSync(link);
  fs.rmdirSync(fixture);
  fs.unlinkSync(path.join(outside, 'sample.txt'));
  fs.rmdirSync(outside);
}
