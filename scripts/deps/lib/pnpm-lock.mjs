import fs from "node:fs";
import path from "node:path";
import { FRONTEND_DIR, ROOT } from "./paths.mjs";

export const NPM_LOCKFILES = [
  path.join(ROOT, "pnpm-lock.yaml"),
  path.join(FRONTEND_DIR, "pnpm-lock.yaml"),
];

export const NPM_PROJECT_DIRS = [ROOT, FRONTEND_DIR];

/**
 * Parse every package@version from the pnpm lockfile packages: section.
 * Includes optional platform packages so inventory is OS-independent.
 */
export function listPackagesFromPnpmLock(lockfilePath) {
  const text = fs.readFileSync(lockfilePath, "utf8");
  const packages = new Map();
  let inPackages = false;

  for (const line of text.split(/\r?\n/)) {
    if (line === "packages:") {
      inPackages = true;
      continue;
    }

    if (inPackages && /^[A-Za-z]/.test(line) && !line.startsWith(" ")) {
      break;
    }

    if (!inPackages) {
      continue;
    }

    const match = line.match(/^ {2}'?(@?[^'@\s]+(?:\/[^'@\s]+)?)@([^':]+)'?:\s*$/);
    if (!match) {
      continue;
    }

    const name = match[1];
    const version = match[2];
    packages.set(`${name}@${version}`, { name, version });
  }

  return [...packages.values()].sort(
    (a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version),
  );
}

/**
 * Aggregate packages from multiple pnpm lockfiles, deduplicating by name@version.
 */
export function listPackagesFromPnpmLocks(lockfilePaths = NPM_LOCKFILES) {
  const packages = new Map();

  for (const lockfilePath of lockfilePaths) {
    if (!fs.existsSync(lockfilePath)) {
      throw new Error(`Missing pnpm lockfile: ${lockfilePath}`);
    }
    for (const pkg of listPackagesFromPnpmLock(lockfilePath)) {
      packages.set(`${pkg.name}@${pkg.version}`, pkg);
    }
  }

  return [...packages.values()].sort(
    (a, b) => a.name.localeCompare(b.name) || a.version.localeCompare(b.version),
  );
}

export function pnpmVirtualStorePackageJsonPath(projectDir, name, version) {
  const folderName = `${name.replace("/", "+")}@${version}`;
  return path.join(
    projectDir,
    "node_modules",
    ".pnpm",
    folderName,
    "node_modules",
    ...name.split("/"),
    "package.json",
  );
}
