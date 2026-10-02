---
name: implement-change
description: Implement authorized non-trivial AssetBlock features, refactors, and bug fixes across backend, frontend, or shared contracts. Do not use for read-only planning or review.
---

# Implement Change

## Load scope

1. Read [repository instructions](../../../AGENTS.md).
2. Read the complete backend and/or frontend `AGENTS.md` selected by those instructions.
3. Inspect neighboring implementation, contracts, and tests before deciding the change shape.

## Execute

- Confirm the required behavior, reuse a correct existing pattern, prefer platform capabilities, and then consider installed dependencies. Add abstractions or packages only for a current need; fewer lines must not mean fewer safeguards.
- Restate the intended outcome and identify affected boundaries. Make reasonable assumptions only when they do not change product scope or public behavior.
- Prefer the smallest coherent change that satisfies the request. Reuse existing stores, services, BFF helpers, schemas, query modules, UI primitives, and test patterns.
- Preserve security, authorization, transaction, idempotency, file-encryption, checkout, and data-retention invariants described by the scoped guides.
- Keep external I/O outside database transactions. Do not hand-edit EF migrations or add packages without required approval.
- Update all affected layers when a contract changes; do not leave backend, BFF, types, validation, and UI behavior inconsistent.
- Definition of done: when a code change makes any statement, command, or claim in `AGENTS.md`, `README.md`, or skills false or outdated, update that documentation in the same change.
- Update the relevant row in `docs/agent-context.md` if a mapped entry point moves or a trust boundary changes. Do not copy implementation details or completion history into the map.

## Verify proportionally

- Test risk, not line count or coverage percentages. Protect ownership, payments, encrypted files, persistence semantics, contracts, and critical journeys; avoid tests that restate implementation or duplicate existing protection.
- Start with the narrowest affected tests or static checks. Broaden to the affected integration suite/build for persistence, HTTP pipeline, DI, concurrency, payments, auth, routing, configuration, or cross-cutting changes. A small isolated change does not require full suites or Playwright.
- For frontend source changes, run `pnpm run check`; add `pnpm run build` when routing, Server Components, configuration, or TypeScript boundaries change. Run relevant Vitest files; use Playwright only for browser behavior that smaller checks cannot cover meaningfully.
- For instructions/Markdown-only changes, inspect links, commands, routing, and the final diff; validate changed skill metadata. Do not run application tests/builds for such changes.
- Reuse checks already run against the final relevant source state. Repeat only when failure, later edits, or unresolved risk invalidate the evidence.
- Review the final diff for unrelated edits, missing error paths, leaked secrets, stale contracts, and unverified assumptions.

## Deliver

- Report concise provenance-backed verification: exact command, exit code, and raw terminal summary/counts (e.g., passed/failed test counts). When claiming a pre-existing failure, provide the baseline command and result.
- Keep the user-facing report concise while ensuring evidence is directly reviewable. Report outcome, key files, and remaining risks or unverified work. Never present skipped verification as passing.
