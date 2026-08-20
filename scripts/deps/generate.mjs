import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { enrichNugetPackages, generateNugetSbom, listNugetPackages } from "./lib/nuget.mjs";
import { generateNpmSbom, listNpmPackages } from "./lib/npm.mjs";
import { buildNoticesMarkdown, evaluatePackages } from "./lib/notices.mjs";
import { loadExceptions, loadPolicy } from "./lib/policy.mjs";
import { BACKEND_SBOM, FRONTEND_SBOM, NOTICES_PATH, SBOM_DIR } from "./lib/paths.mjs";

export async function collectPackages() {
  const nuget = await enrichNugetPackages(listNugetPackages());
  const npm = await listNpmPackages();
  return [...nuget, ...npm].sort(
    (a, b) =>
      a.ecosystem.localeCompare(b.ecosystem) ||
      a.name.localeCompare(b.name) ||
      a.version.localeCompare(b.version),
  );
}

export async function generateAll({ writeNotices = true, writeSboms = true } = {}) {
  const policy = loadPolicy();
  const exceptions = loadExceptions();
  const packages = await collectPackages();
  const licenseErrors = evaluatePackages(packages, policy, exceptions);
  const notices = buildNoticesMarkdown(packages);

  if (licenseErrors.length > 0) {
    return { packages, notices, licenseErrors, policy, exceptions };
  }

  if (writeNotices) {
    fs.writeFileSync(NOTICES_PATH, notices, "utf8");
  }

  if (writeSboms) {
    fs.mkdirSync(SBOM_DIR, { recursive: true });
    generateNugetSbom(BACKEND_SBOM);
    generateNpmSbom(FRONTEND_SBOM, packages);
  }

  return { packages, notices, licenseErrors, policy, exceptions };
}

const isDirectRun = process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url);

if (isDirectRun) {
  const { licenseErrors } = await generateAll();
  if (licenseErrors.length > 0) {
    console.error("License policy violations:\n" + licenseErrors.map((e) => ` - ${e}`).join("\n"));
    process.exitCode = 1;
  } else {
    console.log(`Wrote ${NOTICES_PATH}`);
    console.log(`Wrote ${BACKEND_SBOM}`);
    console.log(`Wrote ${FRONTEND_SBOM}`);
  }
}
