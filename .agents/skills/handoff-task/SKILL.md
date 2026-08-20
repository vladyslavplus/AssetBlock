---
name: handoff-task
description: Prepare a compact, ready-to-paste handoff prompt for continuing an AssetBlock task in Cursor, Claude Code, Codex, or another agent. Use when the user asks to transfer, continue, delegate, or generate a prompt for another coding agent; do not implement the task or create a file unless separately requested.
---

# Handoff Task

Produce a small, self-contained prompt that lets another agent continue without replaying the conversation.

## Build the handoff

- Identify recipient and job type from the request. Default to a tool-neutral recipient; do not assume Cursor unless named.
- Preserve user decisions, intended outcome, current progress, scope, non-goals, acceptance criteria, and unresolved blockers.
- Inspect only artifacts needed to make the handoff accurate. Prefer repository-relative links to plans, specs, ADRs, code, tests, commits, or diffs instead of copying their contents.
- Include working-tree or branch state only when it affects the task. Never claim a command passed without fresh evidence.
- Redact secrets, credentials, private keys, tokens, personal data, payment payloads, and decrypted asset content.
- Do not add requirements, architecture changes, packages, migrations, or external actions that the user did not authorize.

## Route repository guidance

Tell the recipient to read root `AGENTS.md` and the applicable nested backend/frontend guide. Route the task by intent:

- Implementation: `.agents/skills/implement-change/SKILL.md`.
- Review: `.agents/skills/review-change/SKILL.md`.
- Cursor implementation or review: mention the matching `.cursor/agents/` agent only when it exists and fits the task.

Reference these instructions by path. Do not duplicate their checklists in the prompt.

## Output

Return one ready-to-paste Markdown prompt in a fenced block. Keep it as short as completeness permits, using only relevant sections from:

- Outcome
- Current state and decisions
- Scope and non-goals
- Source-of-truth paths
- Acceptance criteria
- Verification
- Open risks or questions

Use imperative language for the recipient. Avoid chat history, narration, generic coding advice, raw diffs, and large file excerpts. If no open risk exists, omit that section.

Return the prompt in chat by default. Save it only when the user explicitly asks for a file; use the user-specified path, or optional ignored `.cursor/plans/` scratch space when no path is supplied.
