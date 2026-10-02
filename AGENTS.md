# AssetBlock Agent Guide

## Instruction routing

- This file applies repository-wide. Use `README.md` for architecture and setup details only when the task needs them.
- Before changing or reviewing backend code, read `asblock-backend/AGENTS.md` completely.
- Before changing or reviewing frontend code, read `asblock-frontend/AGENTS.md` completely.
- For cross-stack work, read both nested guides and keep backend contracts, BFF routes, schemas, query keys, and UI behavior aligned.
- For non-trivial implementation, refactoring, or bug-fixing work, read and follow `.agents/skills/implement-change/SKILL.md`.
- For implementing end-to-end features spanning backend, BFF, and frontend, read and follow `.agents/skills/add-feature-slice/SKILL.md`.
- For database migrations, read and follow `.agents/skills/add-migration/SKILL.md`.
- When asked to review a plan, implementation, architecture, or project, read and follow `.agents/skills/review-change/SKILL.md`. Route backend and frontend work to their separate review lanes.
- When asked to prepare a prompt, handoff, or continuation package for another agent or chat, read and follow `.agents/skills/handoff-task/SKILL.md`.
- `.cursor/plans/` is optional ignored scratch space for local plans. Do not depend on a specific plan file or reference one from tracked source files.

## Working agreement

- Distinguish planner, reviewer, and implementer roles. Planning and review are read-only; implement only when the user authorizes changes. Existing authorization remains valid for its scope.
- Inspect relevant code and tests before editing. Preserve user changes and keep diffs focused on the requested outcome.
- Follow existing architecture and feature patterns. Avoid speculative abstractions, parallel service layers, broad renames, and unrelated cleanup.
- Prefer a correct existing pattern, then language/framework capabilities, then an installed dependency before adding new code or packages. Do not remove validation, authorization, accessibility, error handling, or requested behavior to make code smaller.
- Do not add dependencies, alter public contracts, change database schema, or generate migrations unless the task requires it. Follow the approval and migration rules in the relevant nested guide.
- Never commit secrets or expose tokens, credentials, private keys, payment payloads, or decrypted asset content.
- Use the narrowest verification that gives meaningful confidence. Escalate to broader builds or tests for cross-cutting, security-sensitive, persistence, routing, or configuration changes.
- If verification cannot run, report exactly what remains unverified and why.
- Do not commit, push, deploy, or install external agent tools unless requested.

## Context and decisions

- Use `docs/agent-context.md` to locate the relevant feature; confirm its entry points against current source. Local retrieval/tool setup is in `docs/agent-tools.md`; read it only when needed.
- Prefer scoped Serena symbol retrieval when available; fall back to targeted `rg` and reads. Do not perform automatic onboarding scans or load all memories. HTTP routes, DI, authorization, and persistence boundaries still require source inspection.
- Repomix packages are optional handoff snapshots for another environment, not default context for every task. Package explicit feature paths only when requested; preserve source provenance and recheck changed facts.
- Load only the guides, skills, and references relevant to the task. Reuse instructions already read in this conversation unless they changed or context was compacted.
- Start with the requested scope, Git state, relevant call sites, and focused tests. Use targeted searches and file sections before scanning the repository. Widen only to resolve a concrete dependency or uncertainty.
- Reuse prior findings, handoffs, and verification evidence with their commit/file provenance. Recheck facts affected by intervening changes; never treat an old summary or code index as current source truth.
- `README.md` describes architecture/setup. Use tracked documentation for durable product decisions; local excluded files are not repository guidance. Neither an ignored plan nor a completion checkbox proves implementation, passing verification, or reviewer approval. Keep these statuses separate and surface material conflicts.
- Durable product decisions belong in tracked documentation when requested. Do not create a new documentation tree or duplicate instructions merely to retain chat history.

## Review execution boundary

- Planners and reviewers inspect source, diffs, plans, and existing evidence only. They must not run tests, builds, linters, formatters, restores, migrations, generators, package audits, dependency checks, applications, smoke tests, or other project verification commands.
- Verification execution belongs to the implementer. Reviewers may inspect test code and report implementer-provided results, clearly marking them as not independently rerun.
- When requested, reviewers may probe local agent-tool availability with Serena configuration/scoped symbol queries and Repomix `--version`/`--help`. This exception does not authorize onboarding, full indexing, package generation, installation, or application verification.

## Delivery

- Review findings go in one copyable prompt for the existing implementer, as defined by `review-change`; do not duplicate them outside the prompt. Planning handoffs start with `# Context` and preserve scope, authorization, decisions, and evidence limits.
- When wrapping content containing fenced code blocks, use a longer outer fence or indent commands.
- Summarize the outcome, key files, verification commands/results, and remaining risks or follow-ups.
- Do not claim completion when required behavior or verification is still missing.
