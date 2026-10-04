import assert from "node:assert/strict";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import test from "node:test";
import { createRequire } from "node:module";
import { BRACES_ADVISORY, verifyBracesRemediation, isVerifiedBracesFinding } from "./braces-remediation.mjs";
import {
  interpretNpmAuditResult,
  isTransientAuditError,
  listNpmVulnerabilities,
  parseNpmAuditJson,
  resolveCanonicalNpmMetadata,
} from "./npm.mjs";
import { evaluatePackages, filterSevereVulnerabilities } from "./notices.mjs";
import { validateExceptionEntry } from "./policy.mjs";
import {
  listPackagesFromPnpmLock,
  listPackagesFromPnpmLocks,
  NPM_LOCKFILES,
  NPM_PROJECT_DIRS,
} from "./pnpm-lock.mjs";
import { buildBoundedLineDiff } from "./diff.mjs";

test("braces remediation verifies both installed consumers and preserves glob behavior", async (t) => {
  const proof = verifyBracesRemediation();
  assert.equal(proof.projects.length, 2);
  for (const projectDir of proof.projects) {
    await t.test(path.basename(projectDir), () => {
      const require = createRequire(path.join(projectDir, "node_modules/.pnpm/micromatch@4.0.8/node_modules/micromatch/package.json"));
      const braces = require("braces");
      const micromatch = require("micromatch");
      assert.deepEqual(braces.expand("src/{a,b}/{1..3}.ts"), ["src/a/1.ts", "src/a/2.ts", "src/a/3.ts", "src/b/1.ts", "src/b/2.ts", "src/b/3.ts"]);
      assert.deepEqual(braces.expand("{a,{b,c}}"), ["a", "b", "c"]);
      assert.equal(braces.stringify(braces.parse("a/{b,c}/d")), "a/{b,c}/d");
      assert.deepEqual(micromatch(["src/a.ts", "src/b.js", "src/c.py"], "src/*.{js,ts}"), ["src/a.ts", "src/b.js"]);
      const depthError = { name: "SyntaxError", message: "Brace AST exceeds maximum nesting depth of 64" };
      for (const method of ["parse", "compile", "expand", "stringify"]) {
        for (const [open, close] of [["{", "}"], ["(", ")"]]) {
          assert.throws(() => braces[method](open.repeat(4900) + "a,b" + close.repeat(4900)), depthError);
        }
      }
      for (const method of ["compile", "expand", "stringify"]) {
        const root = { type: "root", nodes: [] };
        let current = root;
        for (let i = 0; i < 5000; i++) {
          const child = { type: "root", nodes: [], parent: current };
          current.nodes.push(child);
          current = child;
        }
        assert.throws(() => braces[method](root), depthError);
        const cyclic = { type: "root", nodes: [] };
        cyclic.nodes.push(cyclic);
        assert.throws(() => braces[method](cyclic), depthError);
      }
    });
  }
});

test("braces remediation fails closed for patch, lock, configuration and installed source drift", (t) => {
  const original = verifyBracesRemediation().projects[0];
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "braces-remediation-"));
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const patchPath = path.join(temp, "braces.patch");
  fs.copyFileSync(path.resolve(original, "../patches/braces@3.0.3.patch"), patchPath);
  const lockPath = path.join(temp, "pnpm-lock.yaml");
  fs.copyFileSync(path.join(original, "pnpm-lock.yaml"), lockPath);
  const workspacePath = path.join(temp, "pnpm-workspace.yaml");
  fs.writeFileSync(workspacePath, "patchedDependencies:\n  braces@3.0.3: braces.patch\n");
  const parentDir = path.join(temp, "node_modules/.pnpm/micromatch@4.0.8/node_modules");
  fs.mkdirSync(path.join(parentDir, "micromatch"), { recursive: true });
  fs.writeFileSync(path.join(parentDir, "micromatch/package.json"), '{"name":"micromatch","version":"4.0.8"}');
  const require = createRequire(path.join(original, "node_modules/.pnpm/micromatch@4.0.8/node_modules/micromatch/package.json"));
  fs.cpSync(path.dirname(require.resolve("braces/package.json")), path.join(parentDir, "braces"), { recursive: true });
  const verify = () => verifyBracesRemediation({ projectDirs: [temp], patchPath });
  assert.equal(verify().verified, true);
  for (const [file, mutate, error] of [
    [patchPath, (text) => text + "tampered", /patch hash mismatch/],
    [lockPath, (text) => text.replaceAll("braces: 3.0.3(patch_hash=", "braces: 3.0.3(unpatched="), /Unverified braces dependency/],
    [workspacePath, () => "patchedDependencies: {}", /patch configuration/],
    [path.join(parentDir, "braces/lib/compile.js"), (text) => text.replace("depth > 64", "false"), /Unpatched or modified/],
  ]) {
    const before = fs.readFileSync(file, "utf8");
    fs.writeFileSync(file, mutate(before));
    assert.throws(verify, error);
    fs.writeFileSync(file, before);
  }
});

test("local braces fix never suppresses other advisories or unverified evidence", () => {
  const finding = { ecosystem: "npm", name: "braces", version: "3.0.3", advisoryUrl: BRACES_ADVISORY };
  const proof = verifyBracesRemediation();
  assert.equal(isVerifiedBracesFinding(finding, proof), true);
  assert.equal(isVerifiedBracesFinding(finding, { verified: false }), false);
  for (const change of [{ ecosystem: "nuget" }, { name: "micromatch" }, { version: "3.0.2" }, { advisoryUrl: "https://example.test/new-advisory" }]) {
    assert.equal(isVerifiedBracesFinding({ ...finding, ...change }, proof), false);
  }
});

test("parseNpmAuditJson_WhenHighDevAdvisory_ShouldSurfaceFinding", () => {
  const findings = parseNpmAuditJson(
    JSON.stringify({
      advisories: {
        "999": {
          id: 999,
          module_name: "eslint",
          severity: "high",
          url: "https://example.test/advisory",
          findings: [{ version: "9.0.0", dev: true }],
        },
      },
    }),
  );

  assert.equal(findings.length, 1);
  assert.equal(findings[0].name, "eslint");
  assert.equal(findings[0].severity, "high");
  assert.equal(findings[0].development, true);

  const severe = filterSevereVulnerabilities(findings, {
    failVulnerabilitySeverities: ["High", "Critical"],
  });
  assert.equal(severe.length, 1);
});

test("parseNpmAuditJson_WhenEmptyVulnerabilities_ShouldSucceed", () => {
  const findings = parseNpmAuditJson(
    JSON.stringify({
      vulnerabilities: {},
      metadata: { vulnerabilities: { info: 0, low: 0, moderate: 0, high: 0, critical: 0 } },
    }),
  );
  assert.deepEqual(findings, []);
});

test("parseNpmAuditJson_WhenRegistryErrorPayload_ShouldThrow", () => {
  assert.throws(
    () =>
      parseNpmAuditJson(
        JSON.stringify({
          error: {
            code: "ERR_PNPM_META_FETCH_FAIL",
            summary: "GET https://registry.npmjs.org/: request failed",
          },
        }),
      ),
    /ERR_PNPM_META_FETCH_FAIL/,
  );
});

test("parseNpmAuditJson_WhenUnknownSchema_ShouldThrow", () => {
  assert.throws(
    () => parseNpmAuditJson(JSON.stringify({ ok: true, message: "not an audit report" })),
    /unrecognized JSON schema/,
  );
});

test("interpretNpmAuditResult_WhenNonZeroWithoutFindings_ShouldThrow", () => {
  assert.throws(
    () =>
      interpretNpmAuditResult({
        status: 1,
        stdout: JSON.stringify({ vulnerabilities: {} }),
        stderr: "",
        cwd: "/tmp/demo",
      }),
    /without vulnerability findings/,
  );
});

test("interpretNpmAuditResult_WhenNonZeroWithHighFinding_ShouldReturnFindings", () => {
  const findings = interpretNpmAuditResult({
    status: 1,
    stdout: JSON.stringify({
      vulnerabilities: {
        eslint: {
          name: "eslint",
          severity: "high",
          via: [],
          range: "9.0.0",
          versions: ["9.0.0"],
          url: "https://example.test/advisory",
          dev: true,
        },
      },
    }),
    stderr: "",
    cwd: "/tmp/demo",
  });

  assert.equal(findings.length, 1);
  assert.equal(findings[0].severity, "high");

  const severe = filterSevereVulnerabilities(findings, {
    failVulnerabilitySeverities: ["High", "Critical"],
  });
  assert.equal(severe.length, 1);
});

test("interpretNpmAuditResult_WhenNonZeroRegistryErrorJson_ShouldThrowInfrastructureFailure", () => {
  assert.throws(
    () =>
      interpretNpmAuditResult({
        status: 1,
        stdout: JSON.stringify({
          error: {
            code: "ERR_PNPM_META_FETCH_FAIL",
            summary: "registry unavailable",
          },
        }),
        stderr: "network error",
        cwd: "/tmp/demo",
      }),
    /ERR_PNPM_META_FETCH_FAIL/,
  );
});

test("isTransientAuditError_WhenTimeoutOrRegistryFailure_ShouldReturnTrue", () => {
  assert.equal(
    isTransientAuditError(new Error("pnpm audit failed: 23: The operation was aborted due to timeout")),
    true,
  );
  assert.equal(
    isTransientAuditError(new Error("pnpm audit failed: ERR_PNPM_META_FETCH_FAIL: registry unavailable")),
    true,
  );
  assert.equal(isTransientAuditError(new Error("ETIMEDOUT: connection timed out")), true);
  assert.equal(isTransientAuditError(new Error("fetch failed: 503 Service Unavailable")), true);
});

test("isTransientAuditError_WhenPermanentError_ShouldReturnFalse", () => {
  assert.equal(isTransientAuditError(new Error("SyntaxError: Unexpected token")), false);
  assert.equal(
    isTransientAuditError(new Error("pnpm audit exited with status 1 without vulnerability findings")),
    false,
  );
  assert.equal(isTransientAuditError(null), false);
});

test("interpretNpmAuditResult_WhenEmptySuccessfulOutput_ShouldThrow", () => {
  assert.throws(
    () =>
      interpretNpmAuditResult({
        status: 0,
        stdout: "",
        stderr: "",
        cwd: "/tmp/demo",
      }),
    /empty output/,
  );
});

test("listNpmVulnerabilities_WhenRegistryUnavailableAfterRetries_ShouldThrow", () => {
  assert.throws(
    () =>
      listNpmVulnerabilities({
        projectDirs: ["/tmp/demo-root"],
        maxAttempts: 1,
        allowRegistryFailures: false,
        runAudit: () => {
          throw new Error("pnpm audit failed: ERR_PNPM_META_FETCH_FAIL: registry unavailable");
        },
      }),
    /ERR_PNPM_META_FETCH_FAIL/,
  );
});

test("listNpmVulnerabilities_WhenRegistryUnavailableByDefaultAfterRetries_ShouldThrow", () => {
  let attempts = 0;
  assert.throws(
    () =>
      listNpmVulnerabilities({
        projectDirs: ["/tmp/demo-root"],
        maxAttempts: 3,
        runAudit: () => {
          attempts += 1;
          throw new Error("pnpm audit failed: ERR_PNPM_META_FETCH_FAIL: registry unavailable");
        },
      }),
    /ERR_PNPM_META_FETCH_FAIL/,
  );
  assert.equal(attempts, 3);
});

test("listNpmVulnerabilities_WhenHighFindingReported_ShouldReturnFinding", () => {
  const findings = listNpmVulnerabilities({
    projectDirs: ["/tmp/demo-root"],
    maxAttempts: 1,
    runAudit: () =>
      interpretNpmAuditResult({
        status: 1,
        stdout: JSON.stringify({
          vulnerabilities: {
            eslint: {
              name: "eslint",
              severity: "high",
              via: [],
              range: "9.0.0",
              versions: ["9.0.0"],
              url: "https://example.test/advisory",
              dev: true,
            },
          },
        }),
        stderr: "",
        cwd: "/tmp/demo-root",
      }),
  });

  assert.equal(findings.length, 1);
  assert.equal(findings[0].severity, "high");
});

test("listNpmVulnerabilities_WhenAllowRegistryFailuresAndTransientError_ShouldSkipRoot", () => {
  const findings = listNpmVulnerabilities({
    projectDirs: ["/tmp/demo-root"],
    maxAttempts: 1,
    allowRegistryFailures: true,
    runAudit: () => {
      throw new Error("pnpm audit failed: ERR_PNPM_META_FETCH_FAIL: registry unavailable");
    },
  });

  assert.deepEqual(findings, []);
});

test("npmGovernanceRoots_WhenConfigured_ShouldIncludeAgentsLockfile", () => {
  assert.ok(NPM_PROJECT_DIRS.some((dir) => dir.replace(/\\/g, "/").endsWith("scripts/agents")));
  assert.ok(NPM_LOCKFILES.some((file) => file.replace(/\\/g, "/").endsWith("scripts/agents/pnpm-lock.yaml")));
});

test("validateExceptionEntry_WhenWildcardWithoutFlag_ShouldFail", () => {
  const errors = validateExceptionEntry(
    {
      ecosystem: "npm",
      name: "demo",
      versions: ["*"],
      license: "MIT",
      reason: "too short",
      reviewedOn: "20-08-2026",
    },
    0,
  );

  assert.ok(errors.some((error) => error.includes("allowVersionWildcard")));
  assert.ok(errors.some((error) => error.includes("reason")));
  assert.ok(errors.some((error) => error.includes("reviewedOn")));
});

test("validateExceptionEntry_WhenPypiPinnedEntry_ShouldPass", () => {
  const errors = validateExceptionEntry(
    {
      ecosystem: "pypi",
      name: "scancode-toolkit",
      versions: ["32.5.0"],
      license: "Apache-2.0 AND CC-BY-4.0 AND LicenseRef-scancode-other-permissive AND LicenseRef-scancode-other-copyleft",
      reason: "Isolated feasibility-pilot license-signal scanner only; not a product runtime.",
      reviewedOn: "2026-10-04",
    },
    0,
  );

  assert.deepEqual(errors, []);
});

test("validateExceptionEntry_WhenPinnedValidEntry_ShouldPass", () => {
  const errors = validateExceptionEntry(
    {
      ecosystem: "npm",
      name: "axe-core",
      versions: ["4.11.2"],
      license: "MPL-2.0",
      reason: "Transitive accessibility engine under MPL-2.0; retained with notices.",
      reviewedOn: "2026-08-20",
    },
    0,
  );

  assert.deepEqual(errors, []);
});

test("listPackagesFromPnpmLock_WhenMixedKeys_ShouldParseAndStopAfterPackages", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "asblock-lock-"));
  const lockfilePath = path.join(dir, "pnpm-lock.yaml");
  fs.writeFileSync(
    lockfilePath,
    [
      "lockfileVersion: '9.0'",
      "",
      "packages:",
      "",
      "  husky@9.1.7:",
      "    resolution: {integrity: sha512-test}",
      "",
      "  lint-staged@16.4.0:",
      "    resolution: {integrity: sha512-test}",
      "",
      "  '@img/sharp-win32-x64@0.35.3':",
      "    resolution: {integrity: sha512-win}",
      "",
      "  '@img/sharp-linux-x64@0.35.3':",
      "    resolution: {integrity: sha512-linux}",
      "",
      "  '@img/sharp-darwin-arm64@0.35.3':",
      "    resolution: {integrity: sha512-darwin}",
      "",
      "  lodash@4.17.21:",
      "    resolution: {integrity: sha512-a}",
      "",
      "  lodash@4.17.24:",
      "    resolution: {integrity: sha512-b}",
      "",
      "snapshots:",
      "",
      "  should-not-be-parsed@1.0.0:",
      "    resolution: {integrity: sha512-ignore}",
      "",
    ].join("\n"),
    "utf8",
  );

  const packages = listPackagesFromPnpmLock(lockfilePath);
  assert.deepEqual(
    packages.map((pkg) => `${pkg.name}@${pkg.version}`),
    [
      "@img/sharp-darwin-arm64@0.35.3",
      "@img/sharp-linux-x64@0.35.3",
      "@img/sharp-win32-x64@0.35.3",
      "husky@9.1.7",
      "lint-staged@16.4.0",
      "lodash@4.17.21",
      "lodash@4.17.24",
    ],
  );
});

test("listPackagesFromPnpmLocks_WhenRootAndFrontend_ShouldDeduplicateAndIncludeTooling", () => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "asblock-locks-"));
  const rootLock = path.join(dir, "root-pnpm-lock.yaml");
  const frontendLock = path.join(dir, "frontend-pnpm-lock.yaml");

  fs.writeFileSync(
    rootLock,
    [
      "packages:",
      "",
      "  husky@9.1.7:",
      "    resolution: {integrity: sha512-root}",
      "",
      "  lint-staged@16.4.0:",
      "    resolution: {integrity: sha512-root}",
      "",
      "  shared@1.0.0:",
      "    resolution: {integrity: sha512-shared}",
      "",
    ].join("\n"),
    "utf8",
  );

  fs.writeFileSync(
    frontendLock,
    [
      "packages:",
      "",
      "  next@16.2.11:",
      "    resolution: {integrity: sha512-next}",
      "",
      "  shared@1.0.0:",
      "    resolution: {integrity: sha512-shared}",
      "",
      "  '@img/sharp-linux-x64@0.35.3':",
      "    resolution: {integrity: sha512-linux}",
      "",
    ].join("\n"),
    "utf8",
  );

  const packages = listPackagesFromPnpmLocks([rootLock, frontendLock]);
  assert.deepEqual(
    packages.map((pkg) => `${pkg.name}@${pkg.version}`),
    [
      "@img/sharp-linux-x64@0.35.3",
      "husky@9.1.7",
      "lint-staged@16.4.0",
      "next@16.2.11",
      "shared@1.0.0",
    ],
  );
});

test("resolveCanonicalNpmMetadata_WhenLocalAndRegistryDiffer_ShouldIgnoreLocal", async () => {
  const registry = {
    license: "Apache-2.0",
    author: "Registry Author",
    repository: { url: "git+https://github.com/example/pkg.git" },
  };
  const localWindows = {
    license: "Apache-2.0 AND LGPL-3.0-or-later",
    author: "Local Windows Author",
    repository: { url: "git+https://github.com/example/local-win.git" },
  };

  const fetchRegistry = async () => registry;
  const fromWindowsPath = await resolveCanonicalNpmMetadata("@img/sharp-win32-x64", "0.35.3", {
    fetchRegistry,
    localPkgJson: localWindows,
  });
  const fromLinuxPath = await resolveCanonicalNpmMetadata("@img/sharp-win32-x64", "0.35.3", {
    fetchRegistry,
    localPkgJson: null,
  });

  assert.deepEqual(fromWindowsPath, fromLinuxPath);
  assert.equal(fromWindowsPath.license, "Apache-2.0");
  assert.equal(fromWindowsPath.author, "Registry Author");
  assert.equal(fromWindowsPath.sourceUrl, "https://github.com/example/pkg");
});

test("evaluatePackages_WhenOverrideDetectedLicense_ShouldApplyToAllPlatformMatches", () => {
  const packages = [
    {
      ecosystem: "npm",
      name: "@img/sharp-win32-x64",
      version: "0.35.3",
      license: "Apache-2.0",
      licenseUrl: "https://spdx.org/licenses/Apache-2.0.html",
    },
    {
      ecosystem: "npm",
      name: "@img/sharp-linux-x64",
      version: "0.35.3",
      license: "Apache-2.0",
      licenseUrl: "https://spdx.org/licenses/Apache-2.0.html",
    },
    {
      ecosystem: "npm",
      name: "@img/sharp-darwin-arm64",
      version: "0.35.3",
      license: "Apache-2.0",
      licenseUrl: "https://spdx.org/licenses/Apache-2.0.html",
    },
  ];
  const exceptions = [
    {
      ecosystem: "npm",
      namePattern: "^@img/sharp-(?!libvips)",
      versions: ["0.35.3"],
      license: "Apache-2.0 AND LGPL-3.0-or-later",
      overrideDetectedLicense: true,
      reason: "Registry under-reports LGPL terms versus distributed sharp platform packages.",
      reviewedOn: "2026-08-20",
    },
  ];
  const policy = { allowedLicenses: ["MIT", "Apache-2.0"] };

  const errors = evaluatePackages(packages, policy, exceptions);
  assert.deepEqual(errors, []);
  for (const pkg of packages) {
    assert.equal(pkg.license, "Apache-2.0 AND LGPL-3.0-or-later");
    assert.equal(pkg.licenseOverridden, true);
  }
});

test("evaluatePackages_WhenExceptionWithoutOverride_ShouldNotRewriteDetectedLicense", () => {
  const packages = [
    {
      ecosystem: "npm",
      name: "axe-core",
      version: "4.11.2",
      license: "MPL-2.0",
      licenseUrl: "https://spdx.org/licenses/MPL-2.0.html",
    },
  ];
  const exceptions = [
    {
      ecosystem: "npm",
      name: "axe-core",
      versions: ["4.11.2"],
      license: "MPL-2.0",
      reason: "Transitive accessibility engine under MPL-2.0; retained with notices.",
      reviewedOn: "2026-08-20",
    },
  ];
  const policy = { allowedLicenses: ["MIT", "Apache-2.0"] };

  const errors = evaluatePackages(packages, policy, exceptions);
  assert.deepEqual(errors, []);
  assert.equal(packages[0].license, "MPL-2.0");
  assert.equal(packages[0].licenseOverridden, undefined);
});

test("evaluatePackages_WhenExceptionWithoutOverrideAndDifferentLicense_ShouldNotSilentlyReplace", () => {
  const packages = [
    {
      ecosystem: "npm",
      name: "@img/sharp-linux-x64",
      version: "0.35.3",
      license: "Apache-2.0",
    },
  ];
  const exceptions = [
    {
      ecosystem: "npm",
      namePattern: "^@img/sharp-(?!libvips)",
      versions: ["0.35.3"],
      license: "Apache-2.0 AND LGPL-3.0-or-later",
      reason: "Authorize LGPL retention without rewriting detected Apache-only registry metadata.",
      reviewedOn: "2026-08-20",
    },
  ];
  const policy = { allowedLicenses: ["MIT", "Apache-2.0"] };

  const errors = evaluatePackages(packages, policy, exceptions);
  assert.deepEqual(errors, []);
  assert.equal(packages[0].license, "Apache-2.0");
  assert.equal(packages[0].licenseOverridden, undefined);
});

test("evaluatePackages_WhenPypiExceptionUnusedByNpmInventory_ShouldNotFail", () => {
  const errors = evaluatePackages(
    [
      {
        ecosystem: "npm",
        name: "left-pad",
        version: "1.3.0",
        license: "MIT",
      },
    ],
    { allowedLicenses: ["MIT"] },
    [
      {
        ecosystem: "pypi",
        name: "scancode-toolkit",
        versions: ["32.5.0"],
        license: "Apache-2.0 AND CC-BY-4.0 AND LicenseRef-scancode-other-permissive AND LicenseRef-scancode-other-copyleft",
        reason: "Isolated feasibility-pilot scanner documented in exception schema only.",
        reviewedOn: "2026-10-04",
      },
    ],
  );
  assert.deepEqual(errors, []);
});

test("buildBoundedLineDiff_WhenTextsDiffer_ShouldIncludeChangedLines", () => {
  const diff = buildBoundedLineDiff("alpha\nbeta\n", "alpha\ngamma\n", {
    beforeLabel: "a",
    afterLabel: "b",
  });
  assert.match(diff, /^-beta$/m);
  assert.match(diff, /^\+gamma$/m);
});
