import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { BACKEND_DIR, BACKEND_SLN } from "./paths.mjs";
import { normalizeLicenseToken } from "./policy.mjs";

function runDotnet(args, options = {}) {
  const result = spawnSync("dotnet", args, {
    encoding: "utf8",
    cwd: options.cwd ?? BACKEND_DIR,
    maxBuffer: 32 * 1024 * 1024,
    shell: false,
  });
  if (result.status !== 0) {
    throw new Error(
      `dotnet ${args.join(" ")} failed:\n${result.stdout ?? ""}\n${result.stderr ?? ""}`,
    );
  }
  return result.stdout ?? "";
}

export function listNugetPackages() {
  const stdout = runDotnet([
    "list",
    BACKEND_SLN,
    "package",
    "--include-transitive",
    "--format",
    "json",
  ]);
  const data = JSON.parse(stdout);
  const packages = new Map();

  for (const project of data.projects ?? []) {
    for (const framework of project.frameworks ?? []) {
      for (const pkg of framework.topLevelPackages ?? []) {
        const key = `${pkg.id.toLowerCase()}@${pkg.resolvedVersion}`;
        packages.set(key, {
          ecosystem: "nuget",
          name: pkg.id,
          version: pkg.resolvedVersion,
          direct: true,
        });
      }
      for (const pkg of framework.transitivePackages ?? []) {
        const key = `${pkg.id.toLowerCase()}@${pkg.resolvedVersion}`;
        if (!packages.has(key)) {
          packages.set(key, {
            ecosystem: "nuget",
            name: pkg.id,
            version: pkg.resolvedVersion,
            direct: false,
          });
        }
      }
    }
  }

  return [...packages.values()].sort((a, b) =>
    a.name.localeCompare(b.name) || a.version.localeCompare(b.version),
  );
}

export function listNugetVulnerabilities() {
  const stdout = runDotnet([
    "list",
    BACKEND_SLN,
    "package",
    "--vulnerable",
    "--include-transitive",
    "--format",
    "json",
  ]);
  const data = JSON.parse(stdout);
  const findings = [];

  for (const project of data.projects ?? []) {
    for (const framework of project.frameworks ?? []) {
      for (const pkg of [
        ...(framework.topLevelPackages ?? []),
        ...(framework.transitivePackages ?? []),
      ]) {
        for (const vuln of pkg.vulnerabilities ?? []) {
          findings.push({
            ecosystem: "nuget",
            name: pkg.id,
            version: pkg.resolvedVersion,
            severity: vuln.severity,
            advisoryUrl: vuln.advisoryurl ?? vuln.advisoryUrl ?? null,
          });
        }
      }
    }
  }

  return findings;
}

function parseNuspecMetadata(nuspecXml) {
  const expressionMatch = nuspecXml.match(
    /<license\b[^>]*type\s*=\s*"expression"[^>]*>([^<]+)<\/license>/i,
  );
  let license = null;
  if (expressionMatch) {
    license = normalizeLicenseToken(expressionMatch[1]);
  } else if (/<license\b[^>]*type\s*=\s*"file"/i.test(nuspecXml)) {
    license = null;
  } else {
    const licenseUrlMatch = nuspecXml.match(/<licenseUrl>\s*([^<]+)\s*<\/licenseUrl>/i);
    if (licenseUrlMatch) {
      const url = licenseUrlMatch[1].trim();
      license = /deprecateLicenseUrl/i.test(url) ? null : normalizeLicenseToken(url);
    } else {
      const legacyLicenseMatch = nuspecXml.match(/<license>([^<]+)<\/license>/i);
      if (legacyLicenseMatch) {
        license = normalizeLicenseToken(legacyLicenseMatch[1]);
      }
    }
  }

  const authors = nuspecXml.match(/<authors>\s*([^<]+)\s*<\/authors>/i)?.[1]?.trim() ?? null;
  const projectUrl = nuspecXml.match(/<projectUrl>\s*([^<]+)\s*<\/projectUrl>/i)?.[1]?.trim() ?? null;
  const licenseUrlRaw = nuspecXml.match(/<licenseUrl>\s*([^<]+)\s*<\/licenseUrl>/i)?.[1]?.trim() ?? null;
  const licenseUrl =
    licenseUrlRaw && !/deprecateLicenseUrl/i.test(licenseUrlRaw)
      ? licenseUrlRaw
      : license && /^[A-Za-z0-9.\-+]+$/.test(license)
        ? `https://spdx.org/licenses/${license}.html`
        : null;

  return {
    license,
    author: authors,
    sourceUrl: projectUrl,
    licenseUrl,
  };
}

function nugetGlobalPackagesPath() {
  if (process.env.NUGET_PACKAGES) {
    return process.env.NUGET_PACKAGES;
  }
  return path.join(os.homedir(), ".nuget", "packages");
}

function readLocalNuspec(name, version) {
  const id = name.toLowerCase();
  const candidate = path.join(nugetGlobalPackagesPath(), id, version, `${id}.nuspec`);
  if (!fs.existsSync(candidate)) {
    return null;
  }
  return fs.readFileSync(candidate, "utf8");
}

const licenseCache = new Map();

export async function resolveNugetMetadata(name, version) {
  const cacheKey = `${name.toLowerCase()}@${version}`;
  if (licenseCache.has(cacheKey)) {
    return licenseCache.get(cacheKey);
  }

  const localXml = readLocalNuspec(name, version);
  if (localXml) {
    const metadata = parseNuspecMetadata(localXml);
    licenseCache.set(cacheKey, metadata);
    return metadata;
  }

  const id = name.toLowerCase();
  const url = `https://api.nuget.org/v3-flatcontainer/${id}/${version}/${id}.nuspec`;
  let lastError = null;

  for (let attempt = 1; attempt <= 4; attempt++) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(30_000) });
      if (response.status === 404) {
        const empty = { license: null, author: null, sourceUrl: null, licenseUrl: null };
        licenseCache.set(cacheKey, empty);
        return empty;
      }
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const xml = await response.text();
      const metadata = parseNuspecMetadata(xml);
      licenseCache.set(cacheKey, metadata);
      return metadata;
    } catch (error) {
      lastError = error;
      await new Promise((resolve) => setTimeout(resolve, attempt * 500));
    }
  }

  throw new Error(
    `Failed to resolve NuGet metadata for ${name}@${version}: ${lastError?.message ?? lastError}`,
  );
}

export async function enrichNugetPackages(packages) {
  const concurrency = 8;
  const enriched = new Array(packages.length);
  let nextIndex = 0;

  async function worker() {
    while (nextIndex < packages.length) {
      const index = nextIndex++;
      const pkg = packages[index];
      const metadata = await resolveNugetMetadata(pkg.name, pkg.version);
      enriched[index] = { ...pkg, ...metadata };
    }
  }

  await Promise.all(Array.from({ length: concurrency }, () => worker()));
  return enriched;
}

export function generateNugetSbom(outputPath) {
  fs.mkdirSync(path.dirname(outputPath), { recursive: true });
  const tempDir = fs.mkdtempSync(path.join(os.tmpdir(), "asblock-cdx-"));
  try {
    runDotnet(["tool", "restore"], { cwd: BACKEND_DIR });
    runDotnet(
      [
        "tool",
        "run",
        "dotnet-CycloneDX",
        "--",
        BACKEND_SLN,
        "--filename",
        "backend.cdx.json",
        "--output",
        tempDir,
        "--output-format",
        "Json",
        "--exclude-test-projects",
      ],
      { cwd: BACKEND_DIR },
    );
    const generated = path.join(tempDir, "backend.cdx.json");
    if (!fs.existsSync(generated)) {
      const fallback = path.join(tempDir, "bom.json");
      if (!fs.existsSync(fallback)) {
        throw new Error("CycloneDX did not produce a backend SBOM file.");
      }
      fs.copyFileSync(fallback, outputPath);
    } else {
      fs.copyFileSync(generated, outputPath);
    }
  } finally {
    fs.rmSync(tempDir, { recursive: true, force: true });
  }
}
