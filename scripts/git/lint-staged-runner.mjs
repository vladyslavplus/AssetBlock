#!/usr/bin/env node
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { createRequire } from "node:module";
import { fileURLToPath } from "node:url";

const MODES = new Set(["frontend-code", "frontend-data", "backend-code"]);
const REPO_ROOT = process.env.LINT_STAGED_REPO_ROOT
  ? path.resolve(process.env.LINT_STAGED_REPO_ROOT)
  : path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const FRONTEND_DIR = "asblock-frontend";
const BACKEND_DIR = "asblock-backend";
const STUB_FIXTURES_DIR = path.join(path.dirname(fileURLToPath(import.meta.url)), "test-fixtures");

function fail(message) {
  console.error(message);
  process.exit(1);
}

function assertContained(absPath) {
  const relative = path.relative(REPO_ROOT, absPath);
  if (relative.startsWith("..") || path.isAbsolute(relative)) {
    fail(`Path escapes repository root: ${absPath}`);
  }
}

function dedupeSorted(paths) {
  return [...new Set(paths)].sort((a, b) => a.localeCompare(b));
}

export function isBackendMigrationRelativePath(relToRepoRoot) {
  const normalized = relToRepoRoot.replace(/\\/g, "/");
  return normalized.includes("/Migrations/") && normalized.endsWith(".cs");
}

function toFrontendRelative(absPath) {
  assertContained(absPath);
  const rel = path.relative(path.join(REPO_ROOT, FRONTEND_DIR), absPath).replace(/\\/g, "/");
  if (rel.startsWith("..") || path.isAbsolute(rel)) {
    fail(`Path is outside frontend root: ${absPath}`);
  }
  return rel;
}

function toBackendRelativeOrNull(absPath) {
  assertContained(absPath);
  const rel = path.relative(REPO_ROOT, absPath).replace(/\\/g, "/");
  if (!rel.startsWith(`${BACKEND_DIR}/`)) {
    fail(`Path is outside backend root: ${absPath}`);
  }
  if (isBackendMigrationRelativePath(rel)) {
    return null;
  }
  return rel;
}

function resolveIncomingPaths(argvPaths) {
  const resolved = [];
  for (const raw of argvPaths) {
    if (!raw || raw === "--") {
      continue;
    }
    const abs = path.isAbsolute(raw) ? path.normalize(raw) : path.resolve(REPO_ROOT, raw);
    assertContained(abs);
    if (!fs.existsSync(abs)) {
      fail(`Staged path does not exist: ${raw}`);
    }
    resolved.push(abs);
  }
  return dedupeSorted(resolved);
}

function run(command, args, cwd) {
  const result = spawnSync(command, args, {
    cwd,
    stdio: "inherit",
    shell: false,
    windowsHide: true,
  });
  if (result.error) {
    fail(result.error.message);
  }
  if (result.status !== 0) {
    process.exit(result.status ?? 1);
  }
}

function resolveBin(cwd, packageName, binName, stubFileName) {
  if (stubFileName === "stub-eslint.mjs" && process.env.LINT_STAGED_STUB_ESLINT) {
    return process.env.LINT_STAGED_STUB_ESLINT;
  }
  if (process.env.LINT_STAGED_STUB_TOOLS === "1") {
    const stubPath = path.join(STUB_FIXTURES_DIR, stubFileName);
    if (!fs.existsSync(stubPath)) {
      fail(`Missing stub tool fixture: ${stubPath}`);
    }
    return stubPath;
  }
  const require = createRequire(path.join(cwd, "package.json"));
  const pkgJsonPath = require.resolve(`${packageName}/package.json`);
  const pkgDir = path.dirname(pkgJsonPath);
  const pkg = JSON.parse(fs.readFileSync(pkgJsonPath, "utf8"));
  const relBin = pkg.bin?.[binName] ?? pkg.bin;
  if (!relBin) {
    fail(`Cannot resolve bin for ${packageName}`);
  }
  return path.join(pkgDir, relBin);
}

export function runFrontendCode(files) {
  const frontendRoot = path.join(REPO_ROOT, FRONTEND_DIR);
  const relative = files.map(toFrontendRelative);
  const eslint = resolveBin(frontendRoot, "eslint", "eslint", "stub-eslint.mjs");
  const prettier = resolveBin(frontendRoot, "prettier", "prettier", "stub-prettier.mjs");
  run(process.execPath, [eslint, "--fix", "--", ...relative], frontendRoot);
  run(process.execPath, [prettier, "--write", "--", ...relative], frontendRoot);
}

export function runFrontendData(files) {
  const frontendRoot = path.join(REPO_ROOT, FRONTEND_DIR);
  const relative = files.map(toFrontendRelative);
  const prettier = resolveBin(frontendRoot, "prettier", "prettier", "stub-prettier.mjs");
  run(process.execPath, [prettier, "--write", "--", ...relative], frontendRoot);
}

export function runBackendCode(files) {
  const relative = files
    .map(toBackendRelativeOrNull)
    .filter((rel) => rel !== null);
  if (relative.length === 0) {
    return;
  }
  const slnx = path.join(BACKEND_DIR, "asblock-backend.slnx");
  const formatArgs = ["format", slnx, "--no-restore", "--include", ...relative, "--verbosity", "minimal"];
  if (process.env.LINT_STAGED_STUB_TOOLS === "1") {
    run(process.execPath, [path.join(STUB_FIXTURES_DIR, "stub-dotnet.mjs"), ...formatArgs], REPO_ROOT);
  } else {
    run("dotnet", formatArgs, REPO_ROOT);
  }
}

function main() {
  const [mode, ...rawPaths] = process.argv.slice(2);
  if (!MODES.has(mode)) {
    fail(`Unknown lint-staged runner mode: ${mode ?? "(missing)"}`);
  }

  const files = resolveIncomingPaths(rawPaths);
  if (files.length === 0) {
    return;
  }

  if (mode === "frontend-code") {
    runFrontendCode(files);
  } else if (mode === "frontend-data") {
    runFrontendData(files);
  } else if (mode === "backend-code") {
    runBackendCode(files);
  }
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main();
}
