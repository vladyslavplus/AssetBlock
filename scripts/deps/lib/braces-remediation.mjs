import fs from "node:fs";
import path from "node:path";
import { createHash } from "node:crypto";
import { createRequire } from "node:module";
import { ROOT } from "./paths.mjs";
import { NPM_PROJECT_DIRS, listPackagesFromPnpmLock } from "./pnpm-lock.mjs";

export const BRACES_ADVISORY = "https://github.com/advisories/GHSA-vfj7-8cjw-p6xm";
export const BRACES_PATCH_HASH = "9d9b16d472a54763f75309f1aaf3cfc4a7b8d035b0d40e28173db4f68138821a";
const patchedFiles = {
  "lib/parse.js": "16b2ea734bba964f4c36c9b6d34e29ca74556636ba77232774b2a3e67d671667",
  "lib/compile.js": "0f0ed2056852b5eabb5d60dca58511a5d5ee4379d9ced95e2a04454ba6f45426",
  "lib/expand.js": "95142ab082bb9b76e81b79f0e1bd7cd5d2892f5c83056ce4e951d89594d24515",
  "lib/stringify.js": "3e1a562a76e7d27c76563b8970698b0af3d3a22ebb87a9f8203a402f131651e1",
};

function hash(file) {
  return createHash("sha256").update(fs.readFileSync(file, "utf8").replace(/\r\n/g, "\n")).digest("hex");
}

/** Verify the exact local fix, not a package-version or advisory allowlist. */
export function verifyBracesRemediation({
  projectDirs = NPM_PROJECT_DIRS,
  patchPath = path.join(ROOT, "patches/braces@3.0.3.patch"),
} = {}) {
  if (hash(patchPath) !== BRACES_PATCH_HASH) {
    throw new Error("braces security patch hash mismatch");
  }
  const projects = [];
  for (const projectDir of projectDirs) {
    const lockPath = path.join(projectDir, "pnpm-lock.yaml");
    const braces = listPackagesFromPnpmLock(lockPath).filter((pkg) => pkg.name === "braces");
    if (braces.length === 0) continue;
    if (braces.some((pkg) => pkg.version !== "3.0.3")) {
      throw new Error(`Unsupported braces version in ${lockPath}; re-review the remediation`);
    }
    const lock = fs.readFileSync(lockPath, "utf8").replace(/\r\n/g, "\n");
    if (!lock.includes(`patchedDependencies:\n  braces@3.0.3: ${BRACES_PATCH_HASH}`)) {
      // Lockfiles can have other patched dependencies before braces.
      const patchSection = lock.split("patchedDependencies:")[1]?.split(/\n\S/)[0];
      if (!patchSection?.includes(`\n  braces@3.0.3: ${BRACES_PATCH_HASH}`)) {
        throw new Error(`Missing pinned braces patch in ${lockPath}`);
      }
    }
    const workspace = fs.readFileSync(path.join(projectDir, "pnpm-workspace.yaml"), "utf8");
    const configuredPath = workspace.match(/^  braces@3\.0\.3: (.+)$/m)?.[1]?.trim();
    if (!configuredPath || path.resolve(projectDir, configuredPath) !== path.resolve(patchPath)) {
      throw new Error(`Missing shared braces patch configuration in ${projectDir}`);
    }
    const snapshots = lock.split("\nsnapshots:\n")[1];
    if (!snapshots) throw new Error(`Missing snapshots in ${lockPath}`);
    let consumer;
    let consumers = 0;
    for (const line of snapshots.split(/\r?\n/)) {
      const header = line.match(/^ {2}(\S.+):$/);
      if (header) consumer = header[1];
      if (!/^ {6}braces:/.test(line)) continue;
      if (line.trim() !== `braces: 3.0.3(patch_hash=${BRACES_PATCH_HASH})` || consumer !== "micromatch@4.0.8") {
        throw new Error(`Unverified braces dependency in ${lockPath}: ${consumer}`);
      }
      consumers++;
      const parent = path.join(projectDir, "node_modules/.pnpm/micromatch@4.0.8/node_modules/micromatch/package.json");
      const require = createRequire(parent);
      const packagePath = require.resolve("braces/package.json");
      if (JSON.parse(fs.readFileSync(packagePath, "utf8")).version !== "3.0.3") {
        throw new Error(`Unexpected installed braces version in ${projectDir}`);
      }
      for (const [relativePath, expected] of Object.entries(patchedFiles)) {
        if (hash(path.join(path.dirname(packagePath), relativePath)) !== expected) {
          throw new Error(`Unpatched or modified braces ${relativePath} in ${projectDir}`);
        }
      }
    }
    if (consumers !== 1) throw new Error(`Unexpected braces consumer count in ${lockPath}`);
    projects.push(projectDir);
  }
  return { verified: true, projects };
}

export function isVerifiedBracesFinding(finding, proof) {
  return proof?.verified === true && proof.projects.length > 0
    && finding.ecosystem === "npm" && finding.name === "braces"
    && finding.version === "3.0.3" && finding.advisoryUrl === BRACES_ADVISORY;
}
