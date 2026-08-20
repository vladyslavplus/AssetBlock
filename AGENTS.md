# AssetBlock Agent Guide

## Instruction routing

- This file applies repository-wide. Use `README.md` for architecture and setup details only when the task needs them.
- Before changing or reviewing backend code, read `asblock-backend/AGENTS.md` completely.
- Before changing or reviewing frontend code, read `asblock-frontend/AGENTS.md` completely.
- For cross-stack work, read both nested guides and keep backend contracts, BFF routes, schemas, query keys, and UI behavior aligned.
- For non-trivial implementation, refactoring, or bug-fixing work, read and follow `.agents/skills/implement-change/SKILL.md`.
- `.cursor/plans/` is optional ignored scratch space for local plans. Do not depend on a specific plan file or reference one from tracked source files.

## Working agreement

- Inspect relevant code and tests before editing. Preserve user changes and keep diffs focused on the requested outcome.
- Follow existing architecture and feature patterns. Avoid speculative abstractions, parallel service layers, broad renames, and unrelated cleanup.
- Do not add dependencies, alter public contracts, change database schema, or generate migrations unless the task requires it. Follow the approval and migration rules in the relevant nested guide.
- Never commit secrets or expose tokens, credentials, private keys, payment payloads, or decrypted asset content.
- Use the narrowest verification that gives meaningful confidence. Escalate to broader builds or tests for cross-cutting, security-sensitive, persistence, routing, or configuration changes.
- If verification cannot run, report exactly what remains unverified and why.

## Delivery

- Summarize the outcome, key files, verification commands/results, and remaining risks or follow-ups.
- Do not claim completion when required behavior or verification is still missing.
