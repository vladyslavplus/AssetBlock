# Orchestrate Change

Use $orchestrate-change when you want one coordinator to plan an authorized change, route implementation through your selected provider and transport, and obtain independent read-only review. The skill does not choose a provider, model, reasoning level, or transport for you. If you leave these unspecified, the coordinator keeps the current session configuration. No automatic provider/model fallback occurs.

## Request example

> $orchestrate-change
>
> Implement <outcome> in this repository. Use <provider and transport, or current session>. Choose exact model/reasoning IDs <if changing current settings>. Keep the coordinator as planner, use one implementation writer and one independent read-only reviewer, and work in the current checkout. Acceptance criteria: <criteria>. Read/write boundary: <paths>. Verification allowed: <checks or source-only hold>.

Name only choices you want to set. If a choice is necessary but unresolved, the coordinator will ask; otherwise current session settings remain in force. The coordinator checks supported exact identifiers before launch and confirms effective provider settings from session evidence afterward when available.

## Workflow boundaries

The coordinator prepares a self-contained initial brief, preserves existing changes, and sets exact scope and verification authority. The writer inspects actual callers, consumers, data ownership, and complete predicates before changing code. One independent reviewer reads the same frozen target. Follow-ups carry only new findings and current source identity, not repeated history. The coordinator checks session transcripts and retained artifacts before any retry.

Verification is proportional and follows repository instructions. Reviewers stay read-only. Focused checks precede expensive final checks when authorized; only invalidated checks are repeated. Source approval, runtime behavior, integration, and admission remain separate outcomes. The workflow does not commit, push, deploy, or change branches/worktrees unless explicitly authorized and allowed by repository policy.

## Optional Antigravity adapter

A repository-specific Antigravity CLI adapter remains available, but is not the default orchestration path. Read [references/antigravity.md](references/antigravity.md) only if you explicitly choose that adapter. Its runtime has a model fallback, so adapter invocations require a user-selected exact --model; the docs do not recommend or change that runtime behavior. The [smoke prompt](examples/smoke-prompt.md) is only for its dry-run setup check, not an implementation template.
