import test from "node:test";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
const REPO_ROOT = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..", "..");
const LINT_STAGED_CONFIG = path.join(REPO_ROOT, "lint-staged.config.mjs");
const RUNNER = path.join(REPO_ROOT, "scripts", "git", "lint-staged-runner.mjs");

function runRunner(mode, files, extraEnv = {}) {
  return spawnSync(process.execPath, [RUNNER, mode, ...files], {
    cwd: REPO_ROOT,
    encoding: "utf8",
    shell: false,
    env: { ...process.env, LINT_STAGED_STUB_TOOLS: "1", ...extraEnv },
  });
}

function readStubLog(logPath) {
  if (!fs.existsSync(logPath)) {
    return [];
  }
  return fs
    .readFileSync(logPath, "utf8")
    .trim()
    .split("\n")
    .filter(Boolean)
    .map((line) => JSON.parse(line));
}

test("runner rejects paths outside repository root", () => {
  const outside = path.join(os.tmpdir(), "outside-lint-staged.txt");
  const result = runRunner("frontend-code", [outside]);
  assert.notEqual(result.status, 0);
  assert.match(result.stderr ?? result.stdout, /escapes repository root|outside/i);
});

test("frontend-code passes filenames to eslint and prettier with -- separator", () => {
  const logPath = path.join(os.tmpdir(), `lint-staged-stub-${Date.now()}.log`);
  fs.writeFileSync(logPath, "", "utf8");
  const relFile = "lib/auth/roles.ts";
  const absFile = path.join(REPO_ROOT, "asblock-frontend", relFile);
  assert.ok(fs.existsSync(absFile), "fixture frontend file must exist");

  const result = runRunner("frontend-code", [absFile], { LINT_STAGED_STUB_LOG: logPath });
  assert.equal(result.status, 0, result.stderr ?? result.stdout);

  const entries = readStubLog(logPath);
  const eslint = entries.find((e) => e.tool === "eslint");
  const prettier = entries.find((e) => e.tool === "prettier");
  assert.ok(eslint, "eslint stub should run");
  assert.ok(prettier, "prettier stub should run");
  assert.deepEqual(eslint.argv.slice(0, 2), ["--fix", "--"]);
  assert.deepEqual(prettier.argv.slice(0, 2), ["--write", "--"]);
  assert.equal(eslint.argv.at(-1), relFile);
  assert.equal(prettier.argv.at(-1), relFile);
});

test("frontend-code propagates tool failure as nonzero exit", () => {
  const logPath = path.join(os.tmpdir(), `lint-staged-fail-${Date.now()}.log`);
  const relFile = "lib/auth/roles.ts";
  const absFile = path.join(REPO_ROOT, "asblock-frontend", relFile);
  const failStub = path.join(REPO_ROOT, "scripts", "git", "test-fixtures", "stub-fail.mjs");
  const result = spawnSync(
    process.execPath,
    [RUNNER, "frontend-code", absFile],
    {
      cwd: REPO_ROOT,
      encoding: "utf8",
      shell: false,
      env: {
        ...process.env,
        LINT_STAGED_STUB_TOOLS: "1",
        LINT_STAGED_STUB_LOG: logPath,
        LINT_STAGED_STUB_ESLINT: failStub,
      },
    },
  );
  // Override resolveBin for eslint only via env - runner doesn't support yet
  assert.notEqual(result.status, 0);
});

test("backend-code skips migrations and formats ordinary C# files", () => {
  const logPath = path.join(os.tmpdir(), `lint-staged-backend-${Date.now()}.log`);
  fs.writeFileSync(logPath, "", "utf8");
  const migrationsDir = path.join(REPO_ROOT, "asblock-backend", "AssetBlock.Infrastructure", "Migrations");
  const migration = path.join(
    migrationsDir,
    fs.readdirSync(migrationsDir).find((n) => n.endsWith(".cs") && !n.includes("Designer")),
  );
  const ordinary = path.join(
    REPO_ROOT,
    "asblock-backend",
    "AssetBlock.Infrastructure",
    "Services",
    "AuditWriter.cs",
  );

  const result = runRunner("backend-code", [migration, ordinary], { LINT_STAGED_STUB_LOG: logPath });
  assert.equal(result.status, 0, result.stderr ?? result.stdout);

  const dotnet = readStubLog(logPath).find((e) => e.tool === "dotnet");
  assert.ok(dotnet, "dotnet format should run for mixed batch");
  const includeIndex = dotnet.argv.indexOf("--include");
  const afterInclude = dotnet.argv.slice(includeIndex + 1);
  const verbosityIndex = afterInclude.indexOf("--verbosity");
  const formattedPaths =
    verbosityIndex === -1 ? afterInclude : afterInclude.slice(0, verbosityIndex);
  assert.equal(formattedPaths.length, 1);
  assert.match(formattedPaths[0], /AuditWriter\.cs$/);
  assert.ok(!formattedPaths.some((p) => p.includes("Migrations")));
});

test("backend-code migration-only batch succeeds without dotnet format", () => {
  const logPath = path.join(os.tmpdir(), `lint-staged-mig-only-${Date.now()}.log`);
  fs.writeFileSync(logPath, "", "utf8");
  const migrationsDir = path.join(REPO_ROOT, "asblock-backend", "AssetBlock.Infrastructure", "Migrations");
  const migration = path.join(
    migrationsDir,
    fs.readdirSync(migrationsDir).find((n) => n.endsWith(".cs") && !n.includes("Designer")),
  );

  const result = runRunner("backend-code", [migration], { LINT_STAGED_STUB_LOG: logPath });
  assert.equal(result.status, 0, result.stderr ?? result.stdout);
  assert.equal(readStubLog(logPath).length, 0);
});

test("lint-staged config uses fixed command strings so filenames are appended by lint-staged", async () => {
  const configUrl = `${pathToFileURL(LINT_STAGED_CONFIG).href}?t=${Date.now()}`;
  const config = await import(configUrl);
  for (const task of Object.values(config.default)) {
    assert.equal(typeof task, "string", "lint-staged tasks must be fixed strings, not functions");
    assert.match(task, /lint-staged-runner\.mjs/);
  }
});

test("lint-staged-style argv forwards multiple staged paths as separate tokens", () => {
  const logPath = path.join(os.tmpdir(), `lint-staged-multi-${Date.now()}.log`);
  fs.writeFileSync(logPath, "", "utf8");
  const fileA = path.join(REPO_ROOT, "asblock-frontend", "lib", "auth", "roles.ts");
  const fileB = path.join(REPO_ROOT, "asblock-frontend", "lib", "auth", "auth-types.ts");
  const result = runRunner("frontend-code", [fileA, fileB], { LINT_STAGED_STUB_LOG: logPath });
  assert.equal(result.status, 0, result.stderr ?? result.stdout);
  const eslint = readStubLog(logPath).find((e) => e.tool === "eslint");
  assert.deepEqual(eslint.argv.slice(-2).sort(), ["lib/auth/auth-types.ts", "lib/auth/roles.ts"]);
});
