---
name: handoff-task
description: Prepare a compact, ready-to-paste handoff prompt for continuing an AssetBlock task in Cursor, Claude Code, Codex, or another agent. Use when the user asks to transfer, continue, delegate, or generate a prompt for another coding agent; do not implement the task or create a file unless separately requested.
---

# Handoff Task

Produce a small, self-contained prompt that lets another agent continue without replaying the conversation.

## Build the handoff

- Identify recipient and job type from the request. Default to a tool-neutral recipient; do not assume Cursor unless named.
- Preserve user decisions, intended outcome, current progress, scope, non-goals, acceptance criteria, and unresolved blockers.
- State the recipient's role and the authorization already granted. Preserve uncommitted changes and explicit commit/push boundaries; do not imply permission to implement a planning-only task.
- Inspect only artifacts needed to make the handoff accurate. Prefer repository-relative links to plans, specs, ADRs, code, tests, commits, or diffs instead of copying their contents.
- Include working-tree or branch state only when it affects the task. Never claim a command passed without fresh evidence.
- Distinguish inspected source, implementer-reported checks, historical evidence, and unverified behavior. Include commit/file provenance when needed to detect stale evidence.
- Give a minimal reading path: applicable instructions, relevant row in `docs/agent-context.md`, entry symbols, affected callers/contracts, and focused tests. Separate established decisions from facts the recipient must recheck.
- For findings, include severity, current file/line evidence, concrete failure condition, expected behavior, and acceptance criteria. Reference existing guidance rather than repeating it.
- A shared checkout usually needs paths and symbols only. For another environment, an explicitly requested Repomix package may help: follow `docs/agent-tools.md`, select only relevant feature paths, attach its provenance, and disclose omitted/untracked files. Do not generate a package automatically or paste the whole package into the prompt.
- State source revision and relevant dirty paths when evidence may go stale. A package is a working-tree snapshot; a commit hash alone does not describe uncommitted content. Tell the recipient what changed since prior findings and what remains unverified.
- Redact secrets, credentials, private keys, tokens, personal data, payment payloads, and decrypted asset content.
- Do not add requirements, architecture changes, packages, migrations, or external actions that the user did not authorize.

## Route repository guidance

Tell the recipient to read root `AGENTS.md` and the applicable nested backend/frontend guide. Route the task by intent:

- Implementation: `.agents/skills/implement-change/SKILL.md`.
- Review: `.agents/skills/review-change/SKILL.md`.
- Cursor implementation or review: mention the matching `.cursor/agents/` agent only when it exists and fits the task.

Reference these instructions by path. Do not duplicate their checklists in the prompt.

## Output

Return one ready-to-paste Markdown prompt beginning with `# Context` in a fenced block. Use a longer outer fence when the prompt contains fenced code blocks. Keep it as short as completeness permits, using only relevant sections from:

- Outcome
- Current state and decisions
- Scope and non-goals
- Source-of-truth paths
- Acceptance criteria
- Verification
- Open risks or questions

Use imperative language for the recipient. Avoid chat history, narration, generic coding advice, raw diffs, and large file excerpts. If no open risk exists, omit that section.

Return the prompt in chat by default. Save it only when the user explicitly asks for a file. Use the user-specified path; otherwise follow the task's durability requirement. `.cursor/plans/` is temporary ignored scratch space, not a canonical decision archive. Do not create a documentation hierarchy without a current need.
