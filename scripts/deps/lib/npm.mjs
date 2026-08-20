import { spawnSync } from "node:child_process";
import { ROOT } from "./paths.mjs";
import { normalizeLicenseToken } from "./policy.mjs";
import {
  listPackagesFromPnpmLocks,
  NPM_LOCKFILES,
  NPM_PROJECT_DIRS,
} from "./pnpm-lock.mjs";
import { writeCycloneDxBom } from "./sbom.mjs";

function resolveCommand(command) {
  if (process.platform !== "win32") {
    return command;
  }
  if (command === "pnpm") {
    return "pnpm.cmd";
  }
  return command;
}

function runPnpmAllowFail(args, options = {}) {
  const pnpmEntry = process.env.npm_execpath;
  if (
    pnpmEntry &&
    (pnpmEntry.endsWith(".js") || pnpmEntry.endsWith(".cjs") || pnpmEntry.endsWith(".mjs"))
  ) {
    return spawnSync(process.execPath, [pnpmEntry, ...args], {
      encoding: "utf8",
      cwd: options.cwd ?? ROOT,
      maxBuffer: 32 * 1024 * 1024,
      shell: false,
      env: options.env ?? process.env,
    });
  }

  const useShell = process.platform === "win32";
  return spawnSync(resolveCommand("pnpm"), args, {
    encoding: "utf8",
    cwd: options.cwd ?? ROOT,
    maxBuffer: 32 * 1024 * 1024,
    shell: useShell,
    env: options.env ?? process.env,
  });
}

function normalizeAuthor(author) {
  if (!author) {
    return null;
  }
  if (typeof author === "string") {
    return author.trim() || null;
  }
  if (typeof author === "object" && author.name) {
    return String(author.name).trim() || null;
  }
  return null;
}

function normalizeRepository(repository, homepage) {
  if (typeof repository === "string" && repository.trim()) {
    return repository.replace(/^git\+/, "").replace(/\.git$/, "");
  }
  if (repository && typeof repository === "object" && repository.url) {
    return String(repository.url).replace(/^git\+/, "").replace(/\.git$/, "");
  }
  if (typeof homepage === "string" && homepage.trim()) {
    return homepage.trim();
  }
  return null;
}

function spdxLicenseUrl(license) {
  if (!license || !/^[A-Za-z0-9.\-+]+$/.test(license)) {
    return null;
  }
  return `https://spdx.org/licenses/${license}.html`;
}

const metadataCache = new Map();

async function fetchNpmRegistryMetadata(name, version) {
  const cacheKey = `${name}@${version}`;
  if (metadataCache.has(cacheKey)) {
    return metadataCache.get(cacheKey);
  }

  const encoded =
    name.startsWith("@")
      ? `${encodeURIComponent(name)}/${version}`
      : `${name}/${version}`;
  const url = `https://registry.npmjs.org/${encoded}`;
  let lastError = null;

  for (let attempt = 1; attempt <= 4; attempt++) {
    try {
      const response = await fetch(url, { signal: AbortSignal.timeout(30_000) });
      if (response.status === 404) {
        metadataCache.set(cacheKey, null);
        return null;
      }
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
      const data = await response.json();
      metadataCache.set(cacheKey, data);
      return data;
    } catch (error) {
      lastError = error;
      await new Promise((resolve) => setTimeout(resolve, attempt * 400));
    }
  }

  throw new Error(
    `Failed to resolve npm metadata for ${name}@${version}: ${lastError?.message ?? lastError}`,
  );
}

function metadataFromPackageJson(pkgJson) {
  if (!pkgJson) {
    return null;
  }
  const license = normalizeLicenseToken(
    Array.isArray(pkgJson.licenses)
      ? pkgJson.licenses.map((item) => item.type ?? item).join(" OR ")
      : pkgJson.license?.type ?? pkgJson.license,
  );
  return {
    license,
    author: normalizeAuthor(pkgJson.author),
    sourceUrl: normalizeRepository(pkgJson.repository, pkgJson.homepage),
    licenseUrl: spdxLicenseUrl(license),
  };
}

/**
 * Resolve canonical npm package metadata from the registry only.
 * Local package.json is never used for notices/SBOM output (OS-dependent installs).
 * Optional localPkgJson may be supplied for tests/validation but does not affect result.
 */
export async function resolveCanonicalNpmMetadata(name, version, {
  fetchRegistry = fetchNpmRegistryMetadata,
  localPkgJson = undefined,
} = {}) {
  const remote = await fetchRegistry(name, version);
  if (!remote) {
    throw new Error(
      `Canonical npm registry metadata missing for ${name}@${version}`,
    );
  }

  const meta = metadataFromPackageJson(remote);
  if (!meta) {
    throw new Error(
      `Canonical npm registry metadata unreadable for ${name}@${version}`,
    );
  }

  // Local metadata is intentionally ignored for output. Presence is allowed for
  // cache/validation callers, but must not change generated notices by OS.
  void localPkgJson;

  return meta;
}

export async function listNpmPackages({
  fetchRegistry = fetchNpmRegistryMetadata,
} = {}) {
  const lockPackages = listPackagesFromPnpmLocks(NPM_LOCKFILES);
  const enriched = new Array(lockPackages.length);
  let nextIndex = 0;
  const concurrency = 8;

  async function worker() {
    while (nextIndex < lockPackages.length) {
      const index = nextIndex++;
      const pkg = lockPackages[index];
      const meta = await resolveCanonicalNpmMetadata(pkg.name, pkg.version, {
        fetchRegistry,
      });

      enriched[index] = {
        ecosystem: "npm",
        name: pkg.name,
        version: pkg.version,
        direct: false,
        license: meta.license ?? null,
        author: meta.author ?? null,
        sourceUrl: meta.sourceUrl ?? null,
        licenseUrl: meta.licenseUrl ?? spdxLicenseUrl(meta.license),
      };
    }
  }

  await Promise.all(Array.from({ length: concurrency }, () => worker()));
  return enriched;
}

export function parseNpmAuditJson(stdout) {
  const text = String(stdout ?? "").trim();
  if (!text) {
    return [];
  }

  let data;
  try {
    data = JSON.parse(text);
  } catch {
    try {
      const lines = text.split(/\r?\n/).filter(Boolean);
      data = JSON.parse(lines[lines.length - 1]);
    } catch (error) {
      throw new Error(`pnpm audit returned non-JSON output: ${error.message}`);
    }
  }

  if (!data || typeof data !== "object" || Array.isArray(data)) {
    throw new Error("pnpm audit returned unexpected JSON (expected an object)");
  }

  if (data.error) {
    const code = data.error.code ?? "unknown";
    const summary =
      data.error.summary ?? data.error.message ?? JSON.stringify(data.error);
    throw new Error(`pnpm audit failed: ${code}: ${summary}`);
  }

  const hasAdvisories =
    Object.prototype.hasOwnProperty.call(data, "advisories") &&
    data.advisories &&
    typeof data.advisories === "object" &&
    !Array.isArray(data.advisories);
  const hasVulnerabilities =
    Object.prototype.hasOwnProperty.call(data, "vulnerabilities") &&
    data.vulnerabilities &&
    typeof data.vulnerabilities === "object" &&
    !Array.isArray(data.vulnerabilities);

  if (!hasAdvisories && !hasVulnerabilities) {
    throw new Error(
      "pnpm audit returned unrecognized JSON schema (expected advisories or vulnerabilities)",
    );
  }

  const findings = [];

  if (hasAdvisories) {
    for (const advisory of Object.values(data.advisories)) {
      if (!advisory || typeof advisory !== "object") {
        continue;
      }
      findings.push({
        ecosystem: "npm",
        name: advisory.module_name ?? advisory.name ?? String(advisory.id ?? "unknown"),
        version: advisory.findings?.[0]?.version ?? advisory.vulnerable_versions ?? "*",
        severity: advisory.severity,
        advisoryUrl: advisory.url ?? null,
        development: Boolean(advisory.findings?.every?.((finding) => finding.dev)),
      });
    }
    return findings;
  }

  for (const [name, vuln] of Object.entries(data.vulnerabilities)) {
    if (!vuln || typeof vuln !== "object") {
      continue;
    }
    findings.push({
      ecosystem: "npm",
      name,
      version: Array.isArray(vuln.versions) ? vuln.versions.join(" || ") : String(vuln.range ?? "*"),
      severity: vuln.severity,
      advisoryUrl: vuln.url ?? null,
      development: Boolean(vuln.dev),
    });
  }

  return findings;
}

/**
 * Interpret one pnpm audit --json process result.
 * Non-zero exit is allowed only when a recognized audit schema reports findings.
 */
export function interpretNpmAuditResult({ status, stdout, stderr, cwd }) {
  const out = (stdout ?? "").trim();
  if (!out) {
    if (status === 0) {
      return [];
    }
    throw new Error(`pnpm audit failed in ${cwd}:\n${stderr ?? ""}`);
  }

  let findings;
  try {
    findings = parseNpmAuditJson(out);
  } catch (error) {
    throw new Error(`pnpm audit failed in ${cwd}: ${error.message}`);
  }

  if (status !== 0 && findings.length === 0) {
    throw new Error(
      `pnpm audit exited with status ${status} in ${cwd} without vulnerability findings:\n${stderr ?? out}`,
    );
  }

  return findings;
}

export function listNpmVulnerabilities() {
  // Audit full graphs (prod + dev) for every pnpm root in the monorepo.
  const findings = [];
  const seen = new Set();

  for (const cwd of NPM_PROJECT_DIRS) {
    const result = runPnpmAllowFail(["audit", "--json"], { cwd });
    for (const finding of interpretNpmAuditResult({
      status: result.status ?? 1,
      stdout: result.stdout,
      stderr: result.stderr,
      cwd,
    })) {
      const key = [
        finding.name,
        finding.version,
        finding.severity,
        finding.advisoryUrl ?? "",
      ].join("|");
      if (seen.has(key)) {
        continue;
      }
      seen.add(key);
      findings.push(finding);
    }
  }

  return findings;
}

export function generateNpmSbom(outputPath, packages) {
  writeCycloneDxBom({
    outputPath,
    name: "asblock-npm",
    version: "0.1.0",
    packages: packages.filter((pkg) => pkg.ecosystem === "npm"),
  });
}
