# Local Agent Tools

The context map is tracked in [agent-context.md](agent-context.md). Serena retrieves selected symbols and references locally; Repomix creates optional feature-scoped snapshots for handoffs. Neither replaces current source, scoped agent instructions, or implementer verification. Token savings are not measured or guaranteed; avoid loading a package when the recipient already has the checkout.

## Installed versions and prerequisites

- Serena (`serena-agent`) **1.7.0**, MIT according to the installed release's metadata and LICENSE. The Python requirement pins the top-level release; transitive Python dependencies are resolved by pip, not fully locked. Check license changes before upgrading; upstream main may differ from this release.
- Repomix **1.18.1**, MIT; isolated package and pnpm lockfile in `scripts/agents/`. Application dependency manifests are unchanged.
- Serena's release pins Roslyn Language Server **5.5.0-2.26078.4**, TypeScript **5.9.3**, and TypeScript Language Server **5.1.3**. Their release packages/licenses remain in the ignored local runtime.
- Windows setup requires Python 3.11–3.14 (installed with 3.12), Node.js 22 or newer, pnpm, Git, .NET 10 runtime, and PowerShell 7 available on PATH. Product dependencies must already be installed for complete type/reference resolution; this setup does not restore or build the application.

Sources: [Serena](https://github.com/oraios/serena), [Serena language support](https://oraios.github.io/serena/01-about/020_programming-languages.html), [Repomix](https://github.com/yamadashy/repomix), [Codex MCP configuration](https://developers.openai.com/codex/mcp).

## Setup and clients

From the repository root:

```powershell
./scripts/agents/setup.ps1
```

Setup installs the isolated Python runtime and locked Repomix dependencies, downloads Serena's language servers without opening the project, and generates local Codex/Cursor MCP entries. No application tests, builds, indexing, benchmarks, or global client configuration changes are performed. Dependency downloads require network access. The provisioning helper uses the pinned Serena release's dependency providers; inspect it when upgrading Serena.

Tracked `.serena/project.yml` selects C# and TypeScript, backend/frontend workspace folders, read-only mode, no activation command, and six retrieval tools. `configure-serena.py` removes editing/interactive base modes from this checkout's isolated Serena configuration and selects planning; run it separately when updating an existing runtime, then reload MCP. The launcher uses stdio, disables dashboard/browser/log windows, and keeps Serena home/downloads in `.agent-tools/serena-home`. Tools cannot edit code, execute shell commands, or write memories. Native agent tools retain their normal permissions and role boundaries.

Generated `.codex/config.toml` and `.cursor/mcp.json` contain machine-specific paths and are ignored. The generator preserves existing client settings and existing `assetblock_serena` entries; adjust that entry manually if its path becomes stale after moving the checkout. Codex loads project configuration only for trusted workspaces. Reload MCP or open a new chat/client session after configuration; tools are not injected into an already running conversation. Cursor loads its project MCP entry through its MCP settings.

On first real MCP use, Serena starts language servers and populates ignored caches. Source retrieval itself is local and does not call an LLM/cloud embedding API. The opt-in smoke check below verifies selected symbols/references, not complete project resolution. Token savings have not been measured.

If Windows Codex fails before shell startup with `setup refresh had errors`, inspect the current sandbox log for the exact ACL target. A `.codex` directory owned by `CodexSandboxOffline` can prevent the signed-in user's setup helper from updating its deny ACL. Restore ownership of that exact directory to the signed-in user from an administrator shell, preserving its DACL and backing it up first; then retry a normal sandbox command. Do not recursively reset repository ACLs or disable sandbox protection. This repair concerns Windows ownership, not Serena or application configuration.

## Optional handoff packages

Prefer paths and symbol names for agents sharing this checkout. Generate a package only when requested for transfer to an environment without equivalent source access:

```powershell
node scripts/agents/pack-context.mjs asblock-backend/AssetBlock.Application/UseCases/Assets/GetSimilarAssets asblock-frontend/components/assets/similar-assets-block.tsx
```

Pass explicit repository-relative files or feature directories; no globs, remote repositories, or whole-stack defaults. The wrapper selects Git-tracked files only, rejects symlinks/junctions in scopes and candidate ancestors, checks canonical repository containment, and caps candidates at 100 files / 1 MiB. New untracked files are omitted; mention them separately or stage them only when authorized. Generated output is ignored under `artifacts/agent-context/`: `context.xml` and `provenance.json` with HEAD, selected scopes, dirty state, candidate paths, and generation time. Each run replaces the previous package.

Security scanning stays enabled; private/env/key/config patterns and generated directories are excluded. Exact forward-slash include entries avoid Repomix 1.18.1's Windows stdin path issue. The wrapper rejects empty packages and unexpected file paths, and records actual included paths alongside candidates in provenance. Inspect warnings and actual package contents before sharing: scanners are not a guarantee that content is safe. Packages preserve comments, bodies, and source line numbers; structural compression is deliberately off because review needs behavior and security checks. Packages do not contain Git diffs/history. Re-read live files before editing or closing findings, particularly if the checkout changed after generation.

## Context discipline

Opt-in checks from repository root (no installation or application verification):

```powershell
.agent-tools/serena/Scripts/python.exe scripts/agents/check-serena.py
node scripts/agents/check-pack-context.mjs
```

The MCP check starts a fresh local stdio server, verifies six tools and planning-only modes, retrieves one C# and one TypeScript symbol, and checks one UI reference. It writes only ignored logs/caches. The packaging check uses synthetic temporary junction fixtures and rejects unsafe scopes; it does not create a Repomix package. Actual packaging requires an explicit scoped request. These executable regression checks belong to implementation; reviewers use only the availability probes allowed by `AGENTS.md`.

Load root and scoped guides once, navigate the relevant feature, retrieve only needed symbol bodies, and inspect boundary contracts/tests. Use native search for HTTP routes, DI, SQL, configuration, and missing references. Keep durable decisions in tracked documentation only when requested; keep handoffs concise with authorization, decisions, source paths, acceptance criteria, and evidence limits. Do not run onboarding scans, create duplicate instruction collections, or repeatedly pack the repository.
