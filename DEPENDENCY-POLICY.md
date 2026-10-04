# Dependency policy

AssetBlock allows third-party dependencies only when their licenses are approved
by default or covered by an explicit, reviewed exception.

## Allowed by default (SPDX)

- `MIT`
- `MIT-0`
- `Apache-2.0`
- `BlueOak-1.0.0`
- `BSD-2-Clause`
- `BSD-3-Clause`
- `BSD-3-Clause-Clear`
- `0BSD`
- `ISC`
- `PostgreSQL`

SPDX expressions are evaluated as follows:

- `A OR B` is allowed when at least one alternative is allowed (or excepted).
- `A AND B` is allowed when every required license is allowed (or excepted).

Exception schema ecosystems are `nuget`, `npm`, and `pypi`. Adding `pypi` to the
schema does **not** add pip inventory, pip-audit, or SBOM coverage to
`pnpm deps:check`. Those checks still cover NuGet and npm only. A `pypi`
exception documents reviewed terms for an isolated install (for example the
feasibility-pilot ScanCode prefix); it is not product-runtime authorization and
is not treated as unused merely because pip packages are absent from the npm/NuGet
inventories.

## Requires an explicit exception

Document an entry in `dependency-exceptions.json` before adding or retaining:

- Copyleft licenses (GPL, LGPL, AGPL, and similar)
- Weak copyleft / secondary licenses (MPL, CDDL, EPL, and similar)
- Source-available or commercial licenses
- Custom / proprietary terms
- Missing, unknown, or unparseable license metadata

Exceptions must include ecosystem, package `name` or `namePattern`, explicit `versions`
(no bare `*` unless `allowVersionWildcard` + `wildcardReason`), license, reason, and ISO
`reviewedOn` date. Do not silently ignore packages with missing metadata.

Set `overrideDetectedLicense: true` only when a reviewed exception must replace the
canonical detected license in notices/SBOM (for example npm registry under-reports
terms that appear in the distributed package). Ordinary exceptions authorize a
non-allowlisted license without rewriting detected metadata.

## Security

Governance checks fail on **High** and **Critical** vulnerabilities reported by:

- `dotnet list package --vulnerable --include-transitive` (NuGet)
- `pnpm audit` for each npm root (repository root tooling, `asblock-frontend/`, and `scripts/agents/`; full graph including devDependencies)

`braces@3.0.3` has no upstream patched release for
[GHSA-vfj7-8cjw-p6xm](https://github.com/advisories/GHSA-vfj7-8cjw-p6xm).
Both consuming npm roots apply `patches/braces@3.0.3.patch` through pnpm
`patchedDependencies`. The patch bounds parser nesting and recursive AST walks
in compile, expand, and stringify to 64 levels, including direct AST inputs.
Excessive nesting throws `SyntaxError`; normal glob syntax remains supported.
The `.patch` file must retain LF line endings so frozen installs use the same hash
on Windows and Linux.

Raw audit results retain this advisory. `deps:check` treats only this exact npm
package/version/advisory as locally remediated after checking the shared patch
hash, both lockfiles and workspace configurations, every braces dependency edge,
and hashes of the four installed source files resolved by the pinned micromatch
consumer. Missing, changed, or unpatched evidence fails the check. Other advisories
(including new braces advisories) still fail at High/Critical. This is a verified
source remediation, not a vulnerability or license exception. Re-review and remove
the local patch when an upstream fix becomes available.

## Tooling (pinned, FOSS)

| Tool | Version / pin | Role |
| --- | --- | --- |
| CycloneDX .NET (`CycloneDX`) | `6.2.0` via `asblock-backend/.config/dotnet-tools.json` | Backend SBOM |
| ReportGenerator (`dotnet-reportgenerator-globaltool`) | `5.5.11` via `asblock-backend/.config/dotnet-tools.json` (Apache-2.0) | Merge per-project Cobertura into one CI coverage report |
| Aspire Dashboard | `mcr.microsoft.com/dotnet/aspire-dashboard:9.0.0` (MIT) | Local OTLP receiver/UI |
| SeaweedFS (local compose) | `chrislusf/seaweedfs:4.42` (Apache-2.0) | Default local S3-compatible encrypted asset storage |
| ClamAV (local compose) | `clamav/clamav:1.4.6` (GPL-2.0) | Local malware scanning daemon for asset processing |
| MinIO (local compose profile `minio`) | `docker.io/pgsty/minio:RELEASE.2026-08-04T00-00-00Z` (anonymous-pullable rebuild; upstream `quay.io/minio/minio` is no longer anonymously pullable) | Compatibility S3-compatible storage for local A/B |
| `pnpm-lock.yaml` parse | packageManager `pnpm@11.13.0` | Canonical OS-neutral npm inventory from root, `asblock-frontend`, and `scripts/agents` lockfiles |
| npm registry metadata | canonical | Author/source/base license for every npm package (OS-independent); missing registry metadata fails generation |
| `overrideDetectedLicense` exceptions | `dependency-exceptions.json` | Reviewed license corrections when registry under-reports distributed terms |
| `scripts/deps` CycloneDX writer | repo scripts | Combined npm (app + tooling) SBOM |
| `dotnet list package` | .NET SDK 10 | NuGet inventory + vulns |
| `pnpm audit` | packageManager `pnpm@11.13.0` | npm vulns for every pnpm root |

`pnpm deps:check` never modifies committed `THIRD-PARTY-NOTICES.md`. On mismatch it
writes `artifacts/dependency-governance/THIRD-PARTY-NOTICES.generated.md` and a
bounded `.diff` for CI inspection.

## Commands

From the repository root (after `pnpm install`, `pnpm install` in `asblock-frontend/` and `scripts/agents/` (`--ignore-scripts`), and `dotnet restore` / `dotnet tool restore` in `asblock-backend/`):

```bash
pnpm deps:generate   # refresh THIRD-PARTY-NOTICES.md and artifacts/sbom/*
pnpm deps:check      # licenses, exceptions, vulnerabilities, notices freshness
pnpm deps:test       # governance unit tests (lockfile parse, audit parse, exceptions)
```

CI runs the same `pnpm deps:test` and `pnpm deps:check` commands.
