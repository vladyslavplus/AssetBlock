# Dependency policy

AssetBlock allows third-party dependencies only when their licenses are approved
by default or covered by an explicit, reviewed exception.

## Allowed by default (SPDX)

- `MIT`
- `Apache-2.0`
- `BSD-2-Clause`
- `BSD-3-Clause`
- `BSD-3-Clause-Clear`
- `0BSD`
- `ISC`
- `PostgreSQL`

SPDX expressions are evaluated as follows:

- `A OR B` is allowed when at least one alternative is allowed (or excepted).
- `A AND B` is allowed when every required license is allowed (or excepted).

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

## Security

Governance checks fail on **High** and **Critical** vulnerabilities reported by:

- `dotnet list package --vulnerable --include-transitive` (NuGet)
- `pnpm audit` for each npm root (repository root tooling and `asblock-frontend/`; full graph including devDependencies)

## Tooling (pinned, FOSS)

| Tool | Version / pin | Role |
| --- | --- | --- |
| CycloneDX .NET (`CycloneDX`) | `6.2.0` via `asblock-backend/.config/dotnet-tools.json` | Backend SBOM |
| `pnpm-lock.yaml` parse | packageManager `pnpm@11.13.0` | Canonical OS-neutral npm inventory from root + `asblock-frontend` lockfiles |
| npm registry metadata | fallback only | License/author/source when local `node_modules` metadata is missing |
| `scripts/deps` CycloneDX writer | repo scripts | Combined npm (app + tooling) SBOM |
| `dotnet list package` | .NET SDK 10 | NuGet inventory + vulns |
| `pnpm audit` | packageManager `pnpm@11.13.0` | npm vulns for every pnpm root |

## Commands

From the repository root (after `pnpm install`, `pnpm install` in `asblock-frontend/`, and `dotnet restore` / `dotnet tool restore` in `asblock-backend/`):

```bash
pnpm deps:generate   # refresh THIRD-PARTY-NOTICES.md and artifacts/sbom/*
pnpm deps:check      # licenses, exceptions, vulnerabilities, notices freshness
pnpm deps:test       # governance unit tests (lockfile parse, audit parse, exceptions)
```

CI runs the same `pnpm deps:test` and `pnpm deps:check` commands.
