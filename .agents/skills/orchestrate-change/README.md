# Codex UI + Antigravity orchestration

This repository supports a UI-first implementation loop:

1. The main Codex task plans and coordinates the change.
2. Antigravity checks permissions, implements in the current checkout, formats once, and runs focused checks.
3. A dedicated read-only Codex subagent reviews the diff before expensive verification.
4. Codex sends actionable findings back to the same Antigravity conversation.
5. After source approval, Antigravity runs the expensive final verification once.
6. The loop stops at final approval or a documented human decision point.

The workflow does not create branches, worktrees, commits, stashes, pushes, or resets. Local run artifacts are stored under the ignored `.agentflow/runs/` directory. The initial baseline includes recoverable copies and hashes of non-ignored untracked files so executor overwrites remain detectable. Baseline capture stops before execution if non-ignored untracked content exceeds 10,000 files or 512 MiB.

## Prerequisites

- Start Codex in the repository root.
- Install and authenticate Antigravity CLI.
- Trust this repository when Antigravity asks.
- Keep repository-specific `AGENTS.md` files and skills available.

On Windows, the adapter checks `agy` on `PATH` and then falls back to:

```text
%LOCALAPPDATA%\agy\bin\agy.exe
```

Check binary discovery, prompt access, and schema access without starting an agent turn:

```powershell
node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs --prompt-file .agents/skills/orchestrate-change/examples/smoke-prompt.md --permission-manifest .agentflow/runs/<run-id>/permissions.json --dry-run
```

## Recommended Codex UI prompt

Invoke the skill explicitly and describe one coherent outcome:

```text
$orchestrate-change

Implement the following change using Antigravity as the executor:

<Describe the required behavior here. Include relevant constraints, examples, and acceptance criteria if known.>

Workflow requirements:
- Keep this Codex task as planner and coordinator.
- Let Antigravity implement and run the required verification.
- Use a dedicated read-only Codex review subagent.
- Send review findings back to the same Antigravity conversation until approved.
- Work in the current checkout. Do not create a branch, worktree, commit, stash, or push.
- Stop and ask me only when the skill's human-decision conditions apply.
- Use Gemini 3.7 Flash Medium for Antigravity unless I specify another model.
- Finish with the changed files, verification results, review rounds, residual risks, and final verdict.
```

Short form:

```text
$orchestrate-change Use Antigravity to implement <task>. Plan it, run the executor, review through a dedicated read-only Codex subagent, return findings to the same executor conversation until approved, and leave the approved changes uncommitted in the current checkout.
```

The default Antigravity model is `gemini-3.7-flash-medium`. Request another model directly in the UI prompt when needed:

```text
$orchestrate-change Use Antigravity model Gemini 3.7 Flash High to implement <task>. Keep the normal orchestration and review loop.
```

Codex resolves the friendly name to an exact slug available from `agy models`. Current examples include `gemini-3.7-flash-high`, `gemini-3.7-flash-medium`, and `claude-sonnet-4-6`; availability can change with the Antigravity account and CLI version.

## Manual adapter usage

First execution:

```powershell
node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs --prompt-file .agentflow/runs/<run-id>/executor-prompt.md --permission-manifest .agentflow/runs/<run-id>/permissions.json
```

This uses `gemini-3.7-flash-medium`. Override it for the whole run:

```powershell
node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs --prompt-file .agentflow/runs/<run-id>/executor-prompt.md --permission-manifest .agentflow/runs/<run-id>/permissions.json --model gemini-3.7-flash-high
```

The command sends the prompt through Antigravity's NDJSON stdin protocol, avoiding Windows command-line length limits. It prints JSON containing `run_dir`, `conversation_id`, and the normalized execution report.

Continue the same Antigravity conversation after review findings:

```powershell
node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs --prompt-file .agentflow/runs/<run-id>/fix-prompt.md --permission-manifest .agentflow/runs/<run-id>/fix-permissions.json --run-dir .agentflow/runs/<run-id>
```

The adapter preserves the run's selected model on continuation. Passing a new `--model` explicitly changes it for that conversation and stores the new choice.

Run the adapter directly with `node`. Do not wrap it in `pnpm`, `npm`, or `yarn`: this script has no package dependencies, while package managers may perform unrelated registry/bootstrap work before it starts.

Optional arguments:

```text
--agy-bin <path>       Explicit Antigravity executable
--schema <path>        Alternate final-response JSON Schema
--run-dir <path>       Reuse a run and its conversation ID
--timeout <duration>   Antigravity print timeout, default 15m
--model <slug>         Antigravity model override; default gemini-3.7-flash-medium
--effort <level>       low, medium, or high
--agent <name>         Antigravity custom agent
--permission-manifest <path>  Required JSON read/write/command ledger
--agy-settings <path>  Alternate Antigravity settings.json path
--dry-run              Validate setup without invoking Antigravity
```

Set `AGY_BIN` when Antigravity is installed in a nonstandard location.

## Permissions

Headless Antigravity cannot display approval prompts. Configure narrow allow rules for commands the AssetBlock implementation actually needs. Keep workspace file writes limited to this repository. Do not use `--dangerously-skip-permissions` as a workaround.

Codex writes a permission manifest beside each executor/fix/final prompt:

```json
{
  "read_paths": ["AGENTS.md"],
  "write_paths": ["asblock-backend/**"],
  "allowed_commands": ["dotnet test asblock-backend/Example.Tests/Example.Tests.csproj"],
  "allowed_command_prefixes": ["git diff"],
  "allowed_command_patterns": ["Move-Item asblock-backend.* asblock-backend.*"],
  "required_verification": ["dotnet test asblock-backend/Example.Tests/Example.Tests.csproj"],
  "required_paths_present": ["asblock-backend/destination.cs"],
  "required_paths_absent": ["asblock-backend/old.cs"]
}
```

All manifest paths are repository-relative and resolved from the active working directory. Never persist a developer-specific drive, home directory, username, or checkout location in the skill, examples, prompts, tests, or run artifacts.

The adapter reads the global Antigravity `settings.json` and checks every path and exact command before spawning `agy`. Missing rules are returned together, before any model turn or edit. This gate is adapter-owned; it does not depend on the executor model calling `list_permissions`.

`required_verification` is phase-specific. `allowed_command_prefixes` and `allowed_command_patterns` are optional and must stay narrowly scoped. Path postconditions are optional but required when acceptance depends on actual creation, relocation, or removal. A completed report is rejected unless each required command has passed raw evidence and every path postcondition holds. Use a new compact manifest for fix and final phases.

Repository source edits should use Antigravity's `replace_file_content` / `multi_replace_file_content`. `write_to_file` targets Antigravity's artifact area and can reject repository paths; do not fall back to shell-writing commands.

Antigravity permission matching may treat argument changes as distinct commands. Prefer stable repository commands such as project-level test scripts. Keep filtered test commands exact when a broad wildcard rule is not supported.

## Verification order

Use this order to avoid repeating expensive suites after review fixes:

```text
implementation + formatting
focused checks
read-only source review
review fixes + invalidated focused checks
final integration/build/check commands
final delta review only if verification changed files
```

The adapter rejects unmanifested command attempts, unresolved permission denials, empty changed-file ledgers after mutations, and a `completed` report that includes failed or not-run verification, a human-decision reason, omitted required verification, missing raw command evidence, or same-lane verification made stale by later edits. Raw NDJSON, the copied permission manifest, and Git evidence remain available in the run directory for diagnosis.

## Troubleshooting

### `agy` is not found

Restart Codex after installing Antigravity so it receives the updated user `PATH`, or set `AGY_BIN`. On Windows, the standard fallback path is detected automatically.

### Authentication or workspace trust is required

Open a normal terminal in this repository and run:

```powershell
agy
```

Complete sign-in and trust the workspace, then exit and retry from Codex UI.

### The command is retrying requests to `registry.npmjs.org`

Stop that command. It used the obsolete package-manager wrapper and has not started Antigravity. Retry with the direct `node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs ...` command shown above. No npm registry access is required by the adapter.

### Review does not approve after repeated fixes

The workflow stops after three fix rounds or after the same material finding survives two attempts. Read the retained run artifacts, resolve the decision manually, then start or continue the workflow with explicit direction.
