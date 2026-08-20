---
name: implement-change
description: Implement non-trivial features, refactors, and bug fixes in the AssetBlock monorepo. Use proactively for multi-file backend, frontend, or cross-stack coding; do not use for read-only analysis or trivial edits.
---

# Implement Change

## Load scope

1. Read [repository instructions](../../../AGENTS.md).
2. Read the complete backend and/or frontend `AGENTS.md` selected by those instructions.
3. Inspect neighboring implementation, contracts, and tests before deciding the change shape.

## Execute

- Restate the intended outcome and identify affected boundaries. Make reasonable assumptions only when they do not change product scope or public behavior.
- Prefer the smallest coherent change that satisfies the request. Reuse existing stores, services, BFF helpers, schemas, query modules, UI primitives, and test patterns.
- Preserve security, authorization, transaction, idempotency, file-encryption, checkout, and data-retention invariants described by the scoped guides.
- Keep external I/O outside database transactions. Do not hand-edit EF migrations or add packages without required approval.
- Update all affected layers when a contract changes; do not leave backend, BFF, types, validation, and UI behavior inconsistent.

## Verify proportionally

- Start with the narrowest affected tests or static checks.
- Run broader backend tests for persistence, HTTP pipeline, DI, concurrency, payments, auth, or cross-cutting changes.
- Run frontend `pnpm run check`; also run `pnpm run build` when routing, Server Components, configuration, or TypeScript boundaries change.
- Review the final diff for unrelated edits, missing error paths, leaked secrets, stale contracts, and unverified assumptions.

## Deliver

Report outcome, key files, commands and results, plus remaining risks or unverified work. Never present skipped verification as passing.
