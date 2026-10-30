---
name: orchestrate-change
description: Coordinate a user-authorized AssetBlock change through a chosen implementation provider and independent read-only review when the user requests delegated implementation and review.
---

# Orchestrate Change

Use this skill when the user asks for coordinated implementation and independent review. The coordinator plans and communicates; one selected implementer writes; one independent reviewer reads the same frozen target. Keep this workflow provider-neutral.

The user chooses provider, model, reasoning level, and transport. Do not prescribe a provider, model, effort, or fallback. When unspecified, keep the current session configuration. Before starting delegated work, inspect the chosen provider's supported exact model/reasoning identifiers and relevant transport instructions. After launch, verify effective settings from provider/session evidence when available; a requested setting or run-state field alone is not proof. Never silently substitute, downgrade, upgrade, or switch providers/models because of quota, failure, or availability. Ask only when an unresolved choice blocks the work.

Use the selected provider's native transport skill or documentation for invocation, session continuation, and result delivery. Read only the relevant provider reference. [Antigravity](references/antigravity.md) documents an optional adapter; it is not the default workflow. Read [README.md](README.md) for invocation guidance when requested.

## Scope and roles

1. Read repository AGENTS.md and all nested guides relevant to the affected code. Inspect current source, focused tests, call sites, and Git status before planning. Work in the current checkout and branch unless the user directs otherwise. Do not create branches/worktrees or commit, push, deploy, or install tools unless authorized.
2. For nontrivial implementation, follow the repository's implement-change skill and include it in the writer's initial brief.
3. Preserve user changes. State the requested outcome, acceptance criteria, affected boundaries, baseline/source identity, relevant guides, read/write scope, and verification authority. Do not broaden scope or infer external permissions from approval to implement.
4. Use one writer per target. Reuse the authorized implementer and reviewer sessions. Do not add parallel workers or extra delegation unless the user explicitly requests that workflow. The reviewer stays read-only under repository rules and reviews the exact target the implementer froze.
5. Keep implementation, source review, focused verification, integration verification, and runtime/admission status distinct. An accepted report, pending response, clean counter, or matching hash is not proof of behavior; hashes establish identity only.

## Briefs, sessions, and handoffs

Make the initial implementer brief self-contained: include the user's request, actual scope, acceptance criteria, constraints and authorization, baseline, relevant instructions, exact read/write boundaries, and permitted checks. Include raw source/evidence needed for decisions, not merely conclusions or names.

For follow-ups, send compact deltas: exact new finding, affected location, current frozen source identity, prior result, changed acceptance detail, and current permission boundary. Do not repeat the full history. Use stable run/conversation identifiers. Before retrying, inspect the provider transcript, queue/session state, and completed artifacts; a completed old report is not a new job. Do not ask the user to ferry messages between coordinator, writer, and reviewer when the selected transport can deliver them.

On pause, compaction, or restart, preserve partial files and drafts. Record exact paths, hashes, session identifiers, outstanding findings, and unrun checks. Reload only the context needed to continue; do not restart completed work or trust ignored handoffs without checking their bytes and provenance.

## Implement and review

Before editing, trace the affected behavior through actual producers, callers, consumers, signatures, return shapes, field owners, authorization/loading paths, and failure lifetime. Compare representative genuine inputs with the complete predicates and expected outputs. Do not infer behavior from method names, comments, aggregate counts, or a single successful result. Check absence versus null, multiplicity, and independent expected/observed ownership when they matter to the contract. Keep analysis proportional; simple changes do not need exhaustive matrices.

Implement one coherent root-cause correction across the affected path. Inspect the full predicate and caller chain so a new check does not sit beside an older contradictory check. Preserve accepted guards and existing contracts. Separate semantic changes from mechanical path, loader, or pin updates. Avoid unrelated cleanup, duplicate architecture, and new trust layers.

Use the repository's [review-change skill](../review-change/SKILL.md) as canonical for finding format and routing. Give the independent reviewer the raw request, accepted scope, baseline, exact frozen source identity, changed files/diff, relevant evidence, verification results, and deferred checks. The first review must be independent of implementation planning. Route one consolidated finding bundle to the same writer; after a fix, send the changed delta and new evidence to the same reviewer. Reviewers do not run project verification when repository instructions reserve it for the implementer.

Findings should identify the root cause, precise location, trigger, impact, minimal correction, and focused expected verification. Close a finding only when the changed source and evidence address its actual cause; do not relabel it, infer closure from version/count/hash, or manufacture approval.

## Progress and blockers

Do not impose a fixed number of review/fix rounds or stop solely because a finding repeats, a check fails, or a worker reports blocked. After several cycles (often around three), make a progress checkpoint: trace any recurring root cause again, check for regressions or unchanged uncertainty, confirm the writer used the right target and evidence, and determine whether scope or required authority changed. Change the correction approach when needed. Continue through six, eight, or more rounds when work remains authorized and each round makes meaningful progress; avoid blind retries.

Stop and report a concrete blocker when it cannot be resolved within current authorization, or when it requires a destructive/external action, dependency, migration/schema/public-contract expansion, conflicting user change, new scope, user decision, or exhausted budget. If a provider or worker fails, inspect retained state and recover the authorized session where possible. Do not switch provider/model or bypass a run limit without explicit authorization. Preserve user-imposed attempt limits. A source-fix loop never authorizes another operational run, retry, or one-shot action.

## Verification and completion

Run checks only when authorized and permitted by repository instructions. Start with focused checks. Where useful, obtain read-only source review before expensive final checks; run expensive checks only after relevant source approval. Repeat only checks invalidated by a failure or later edit. If a source-only hold applies, leave checks unrun and report them accurately; do not claim completion or readiness.

Finish when requested acceptance criteria are implemented, independent review is approved when required, and all permitted required verification passes. Report exact changed paths and source state, evidence and commands/results, review status, unrun checks, and residual risks. Keep source-approved, tested, integrated, runtime-accepted, and admitted states distinct. Follow repository/user commit policy; never commit automatically.
