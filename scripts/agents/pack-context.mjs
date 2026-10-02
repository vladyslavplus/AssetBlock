import fs from 'node:fs';
import path from 'node:path';
import { execFileSync, spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../../', import.meta.url));
const realRoot = fs.realpathSync(root);
const assertLocalPath = (relativePath) => {
  let current = root;
  for (const part of relativePath.split('/')) {
    current = path.join(current, part);
    if (fs.lstatSync(current).isSymbolicLink()) throw new Error(`Symlink/junction paths are unsupported: ${relativePath}`);
  }
  const relativeRealPath = path.relative(realRoot, fs.realpathSync(current));
  if (relativeRealPath === '..' || relativeRealPath.startsWith(`..${path.sep}`) || path.isAbsolute(relativeRealPath)) {
    throw new Error(`Path resolves outside repository: ${relativePath}`);
  }
};
const scopes = process.argv.slice(2);
if (!scopes.length || scopes.includes('--help')) {
  console.log('Usage: node scripts/agents/pack-context.mjs <repository-relative file or feature directory> [...]\nOnly tracked files. Maximum 100 candidates / 1 MiB. No globs or whole-repository defaults.');
  process.exit(scopes.includes('--help') ? 0 : 1);
}
const normalize = (scope) => {
  const value = scope.replaceAll('\\', '/').replace(/\/$/, '');
  if (!value || value === '.' || value.startsWith('-') || path.isAbsolute(value) || /^[A-Za-z]:/.test(value) || value.split('/').some((part) => part === '..' || part === '.') || /[\r\n*?{}!]/.test(value)) {
    throw new Error(`Use an explicit repository-relative feature path: ${scope}`);
  }
  if (['asblock-backend', 'asblock-frontend', '.git', '.agents'].includes(value)) throw new Error(`Select a narrower feature path: ${scope}`);
  return value;
};
const selectedScopes = scopes.map(normalize);
for (const scope of selectedScopes) assertLocalPath(scope);
// This tracked-file snapshot needs repository ignores, not the user's private global ignore file.
const git = (...args) => execFileSync('git', ['-c', 'core.excludesFile=', ...args], { cwd: root, encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 });
const tracked = git('ls-files', '-z').split('\0').filter(Boolean);
const files = tracked.filter((file) => selectedScopes.some((scope) => file === scope || file.startsWith(`${scope}/`)))
  .filter((file) => {
    if (!fs.existsSync(path.join(root, file))) return false;
    assertLocalPath(file);
    return true;
  });
for (const scope of selectedScopes) if (!files.some((file) => file === scope || file.startsWith(`${scope}/`))) throw new Error(`No tracked files at ${scope}; stage authorized new files separately if needed.`);
if (files.some((file) => /[\r\n*?{}!]/.test(file))) throw new Error('Glob operators and newlines in file paths are unsupported.');
const bytes = files.reduce((total, file) => total + fs.statSync(path.join(root, file)).size, 0);
if (files.length > 100 || bytes > 1024 * 1024) throw new Error('Scope exceeds 100 files / 1 MiB. Select narrower paths.');
const outputDir = path.join(root, 'artifacts/agent-context');
fs.mkdirSync(outputDir, { recursive: true });
// Remove obsolete evidence before attempting a new package.
for (const name of ['context.xml', 'provenance.json']) fs.rmSync(path.join(outputDir, name), { force: true });
const head = git('rev-parse', 'HEAD').trim();
const status = git('status', '--porcelain=v1', '--', ...selectedScopes);
// Repomix 1.18.1 stdin paths become backslash glob patterns on Windows and match no files.
// Supply exact Git paths as forward-slash include entries instead.
const configPath = path.join(outputDir, 'selected-files.config.json');
const config = JSON.parse(fs.readFileSync(path.join(root, 'scripts/agents/repomix.config.json'), 'utf8'));
fs.writeFileSync(configPath, JSON.stringify({ ...config, include: files }));
let result;
try {
  result = spawnSync(process.execPath, [path.join(root, 'scripts/agents/node_modules/repomix/bin/repomix.cjs'), '--config', configPath], {
    cwd: root, encoding: 'utf8', maxBuffer: 8 * 1024 * 1024, windowsHide: true,
  });
} finally {
  fs.rmSync(configPath, { force: true });
}
if (result.stdout) process.stdout.write(result.stdout);
if (result.stderr) process.stderr.write(result.stderr);
if (result.error || result.status !== 0) {
  fs.rmSync(path.join(outputDir, 'context.xml'), { force: true });
  throw result.error ?? new Error(`Repomix failed (${result.status}).`);
}
const xml = fs.readFileSync(path.join(outputDir, 'context.xml'), 'utf8');
const decodeAttribute = (value) => value.replace(/&(quot|apos|lt|gt|amp);/g, (_, entity) => ({ quot: '"', apos: "'", lt: '<', gt: '>', amp: '&' })[entity]);
const included = [...xml.matchAll(/<file path="([^"]+)"/g)].map((match) => decodeAttribute(match[1]).replaceAll('\\', '/'));
if (!included.length || included.some((file) => !files.includes(file))) {
  fs.rmSync(path.join(outputDir, 'context.xml'), { force: true });
  throw new Error('Repomix produced an empty package or included files outside the selected candidates.');
}
fs.writeFileSync(path.join(outputDir, 'provenance.json'), `${JSON.stringify({ generatedAt: new Date().toISOString(), head, scopes: selectedScopes, status, candidates: files, included, note: 'Working-tree snapshot; Repomix may omit ignored, binary, oversized, or security-flagged candidates. Check package contents and warnings. Re-read live source before editing.' }, null, 2)}\n`);
