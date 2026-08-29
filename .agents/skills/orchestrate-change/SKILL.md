---
name: orchestrate-change
description: Orchestrate an AssetBlock code change from Codex UI with Codex planning and coordination, Antigravity CLI execution, and an independent Codex review loop. Use when the user asks Codex to plan, delegate implementation to Antigravity, review its changes, and return findings for fixes until approval; do not use for read-only planning or ordinary direct implementation.
---

# Orchestrate Change

Keep the main Codex thread as planner and coordinator. Delegate implementation to Antigravity CLI and review to one dedicated Codex subagent. The user should not need to copy text between tools.

Read [README.md](README.md) only when the user asks how to invoke, install, configure, or troubleshoot this workflow.

## Establish scope

1. Read repository `AGENTS.md` plus every nested guide required by the task scope.
2. Inspect relevant implementation and tests before planning.
3. Create an outcome-first plan with scope, constraints, acceptance criteria, affected boundaries, and verification commands.
4. Inspect `git status --short`. Do not create a branch, worktree, commit, stash, or reset.
5. Preserve existing user changes. If planned edits overlap already-modified files and safe preservation is uncertain, stop and ask the user before invoking Antigravity.
6. Divide verification into two tiers:
   - focused checks that give fast feedback on edited behavior;
   - final expensive checks such as full PostgreSQL suites, broad builds, and full frontend checks.
7. Select the Antigravity model. Use `gemini-3.7-flash-medium` unless the user explicitly requests another available model. Translate friendly names such as “3.7 Flash High” to the exact slug returned by `agy models`; never invent a slug.

## Delegate implementation

Use [execution-report.schema.json](references/execution-report.schema.json) and the adapter at `scripts/run-antigravity.mjs`.

Create a focused English executor prompt in a temporary ignored path under `.agentflow/runs/`. Include:

- the user's raw request;
- the plan and acceptance criteria;
- relevant repository and nested instructions;
- a request to inspect and use `.agents/skills/implement-change/SKILL.md`;
- exact verification expectations;
- the exact planned read/write locations and command lines needed for focused and final verification;
- a requirement to preserve pre-existing changes and avoid commits, branches, worktrees, pushes, resets, and unrelated cleanup;
- a requirement to return the structured execution report.

Create a JSON permission manifest beside the prompt with these arrays:

- `read_paths`: every planned repository read root/file;
- `write_paths`: every planned write root/file;
- `allowed_commands`: all exact commands for focused and final phases;
- `allowed_command_prefixes`: optional narrow prefixes for harmless variable-tail inspection commands such as `git diff`;
- `allowed_command_patterns`: optional Antigravity token-regex patterns scoped to repository operations whose exact spelling may vary, such as a move constrained to repository-relative source and destination tokens;
- `required_verification`: exact commands required in the current turn.
- `required_paths_present` / `required_paths_absent`: optional actual filesystem postconditions for moves, generated files, and removals that a command ledger cannot prove.

Use repository-relative paths rooted at the active working directory in permission manifests, executor prompts, examples, tests, documentation, and persisted run artifacts. The adapter may resolve them internally for execution, but must not persist developer-specific drives, usernames, home directories, or checkout locations.

The adapter reads Antigravity's global `settings.json` and validates the whole manifest before starting the executor using Antigravity's token-prefix/regex command semantics. Missing rules fail before a conversation turn or edit and are returned as one complete list. Do not rely on the model to call `list_permissions`; tool availability and model compliance are not deterministic enough for a safety gate.

For repository edits, instruct Antigravity to use `replace_file_content` / `multi_replace_file_content` and not `write_to_file`, which is reserved for Antigravity artifact paths. Never authorize shell-writing fallbacks such as `Set-Content`, redirection, or heredocs for source files.

For the initial implementation phase, require Antigravity to:

1. implement one coherent unit at a time when the plan spans several subsystems;
2. run formatting once after edits;
3. run focused verification only;
4. report final expensive verification as deferred in `remaining_risks`, not as falsely passed or failed verification entries;
5. make no further edits after its last focused verification command.

Run from repository root:

```powershell
node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs --prompt-file .agentflow/runs/<run-id>/executor-prompt.md --permission-manifest .agentflow/runs/<run-id>/permissions.json --model <model-slug>
```

Invoke the dependency-free Node adapter directly. Never route it through `pnpm`, `npm`, `yarn`, or another package manager; package-manager bootstrap and registry checks can block before Antigravity starts.

The adapter defaults to `gemini-3.7-flash-medium`, records the selected model in run state, and reuses it on continuation unless explicitly overridden. It records baseline and post-run Git evidence under ignored `.agentflow/runs/` and returns a run directory plus Antigravity conversation ID. Treat Git state and command output as authoritative; executor summary is supporting context only.

The adapter rejects any report after an unresolved permission denial, any command attempt absent from the current manifest, or an empty changed-file ledger after successful file mutations. It also rejects a `completed` report when required path postconditions fail, verification contains failed/not-run entries, a human-decision reason remains, a required command is omitted, raw command evidence is missing, or later same-lane edits make verification stale. Treat such output as invalid, not as success.

Do not use Antigravity's `--dangerously-skip-permissions`. If a required command is soft-denied, report the exact denial and ask the user to add a narrow Antigravity permission rule.

## Verify and review

1. Inspect the executor result, current diff, untracked files, and focused verification.
2. Do not rerun implementation verification as reviewer. Verification execution belongs to the executor under repository rules.
3. Spawn one dedicated review subagent before expensive final verification. Give it the raw request, accepted plan, acceptance criteria, baseline evidence, current diff, relevant files, focused verification, and the explicitly deferred final checks.
4. Require the reviewer to read and follow `.agents/skills/review-change/SKILL.md`. It must remain read-only and return its standard verdict and findings.
5. For meaningful cross-stack changes, let that reviewer route independent backend and frontend lanes as required by the review skill.

The first reviewer turn must be independent of planning. Reuse the same reviewer thread for later rounds so it can verify whether its findings were resolved.

## Fix loop

When verdict is `CHANGES REQUESTED`:

1. Build a compact English fix prompt containing exact findings, affected files, and focused verification. Refer to the original request and acceptance criteria already present in the conversation; do not repeat the full history.
2. Continue the same Antigravity conversation and run directory:

```powershell
node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs --prompt-file <absolute-fix-prompt-path> --permission-manifest <absolute-fix-manifest-path> --run-dir <absolute-run-directory>
```

3. Inspect new Git evidence and executor verification.
4. Send the updated diff and results to the existing review subagent.

When the reviewer returns `APPROVE`, run one final verification phase in the same Antigravity conversation:

1. Send only the final command ledger and current source-approval status.
2. Create a final-phase permission manifest whose `required_verification` contains the exact final command ledger.
3. Require each expensive integration suite/build/check at most once unless it fails or later source edits invalidate it.
4. Require no formatting or source/test edits in this phase.
5. If verification fails, send a focused fix prompt, rerun only invalidated checks, then return the changed diff to the same reviewer.
6. If any file changes during final verification, review that delta before finishing.

Stop with `NEEDS HUMAN` rather than continuing when any condition holds:

- three fix rounds completed without approval;
- the same material finding survives two consecutive fix attempts;
- required verification cannot run or remains failing;
- implementation needs a dependency, migration, public-contract expansion, destructive action, or external mutation not authorized by the task;
- existing user changes conflict with required edits;
- Antigravity returns `blocked` or needs interactive input.

Finish only when reviewer verdict is `APPROVE`, final required verification is passing, no final-phase file change escaped review, and the diff still matches the accepted scope. Do not commit, push, merge, reset, or deploy. Report outcome, selected Antigravity model, files, verification, review rounds, residual risks, and the retained run-directory path.
