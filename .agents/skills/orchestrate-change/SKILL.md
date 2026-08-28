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

## Delegate implementation

Use [execution-report.schema.json](references/execution-report.schema.json) and the adapter at `scripts/run-antigravity.mjs`.

Create a focused English executor prompt in a temporary ignored path under `.agentflow/runs/`. Include:

- the user's raw request;
- the plan and acceptance criteria;
- relevant repository and nested instructions;
- a request to inspect and use `.agents/skills/implement-change/SKILL.md`;
- exact verification expectations;
- a requirement to preserve pre-existing changes and avoid commits, branches, worktrees, pushes, resets, and unrelated cleanup;
- a requirement to return the structured execution report.

Run from repository root:

```powershell
node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs --prompt-file <absolute-prompt-path>
```

Invoke the dependency-free Node adapter directly. Never route it through `pnpm`, `npm`, `yarn`, or another package manager; package-manager bootstrap and registry checks can block before Antigravity starts.

The adapter records baseline and post-run Git evidence under ignored `.agentflow/runs/` and returns a run directory plus Antigravity conversation ID. Treat Git state and command output as authoritative; executor summary is supporting context only.

Do not use Antigravity's `--dangerously-skip-permissions`. If a required command is soft-denied, report the exact denial and ask the user to add a narrow Antigravity permission rule.

## Verify and review

1. Inspect the executor result, current diff, untracked files, and reported verification.
2. Do not rerun implementation verification as reviewer. Verification execution belongs to the executor under repository rules.
3. Spawn one dedicated review subagent for the run. Give it the raw request, accepted plan, acceptance criteria, baseline evidence, current diff, relevant files, and executor-provided verification results.
4. Require the reviewer to read and follow `.agents/skills/review-change/SKILL.md`. It must remain read-only and return its standard verdict and findings.
5. For meaningful cross-stack changes, let that reviewer route independent backend and frontend lanes as required by the review skill.

The first reviewer turn must be independent of planning. Reuse the same reviewer thread for later rounds so it can verify whether its findings were resolved.

## Fix loop

When verdict is `CHANGES REQUESTED`:

1. Build a focused English fix prompt containing the unchanged acceptance criteria, exact findings, and required verification.
2. Continue the same Antigravity conversation and run directory:

```powershell
node .agents/skills/orchestrate-change/scripts/run-antigravity.mjs --prompt-file <absolute-fix-prompt-path> --run-dir <absolute-run-directory>
```

3. Inspect new Git evidence and executor verification.
4. Send the updated diff and results to the existing review subagent.

Stop with `NEEDS HUMAN` rather than continuing when any condition holds:

- three fix rounds completed without approval;
- the same material finding survives two consecutive fix attempts;
- required verification cannot run or remains failing;
- implementation needs a dependency, migration, public-contract expansion, destructive action, or external mutation not authorized by the task;
- existing user changes conflict with required edits;
- Antigravity returns `blocked` or needs interactive input.

Finish only when reviewer verdict is `APPROVE`, no required verification is failing or missing, and the diff still matches the accepted scope. Do not commit, push, merge, reset, or deploy. Report outcome, files, verification, review rounds, residual risks, and the retained run-directory path.
