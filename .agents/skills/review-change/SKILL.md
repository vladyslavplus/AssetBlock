---
name: review-change
description: Review current or specified AssetBlock code changes for defects, regressions, security/privacy, performance, contract drift, and missing verification. Use automatically when the user asks to review, inspect, audit, validate, or check a diff, branch, commit, or PR; do not edit code unless the user separately asks for fixes.
---

# Review Change

Perform a read-only, evidence-based review. Report actionable defects and precise remedies without rewriting the implementation.

## Establish target

1. Read [repository instructions](../../../AGENTS.md).
2. Honor an explicit PR, commit, range, or file scope from the user.
3. Otherwise:
   - If the index or working tree has changes, review staged, unstaged, and relevant untracked files together.
   - If it is clean, compare the current branch with the merge-base of the locally available default branch. Do not fetch, pull, or mutate remotes merely to find a target.
   - If no reviewable change exists, say so and request a target.
4. Inspect relevant surrounding code and tests, not only diff hunks. Do not broaden into an unsolicited whole-repository audit.

## Route lanes

- Backend changes: read `asblock-backend/AGENTS.md` and [backend review guidance](references/backend.md).
- Frontend changes: read `asblock-frontend/AGENTS.md` and [frontend review guidance](references/frontend.md).
- Root configuration, CI, or shared contract changes: load only the lanes they can affect.
- Cross-stack changes: review backend and frontend independently, then reconcile HTTP payloads, status/error handling, auth, caching, and user-visible behavior.
- When multi-agent delegation is available, use independent backend and frontend reviewers in parallel for a meaningful cross-stack diff. Give each reviewer the raw request, target, its lane diff, and its scoped instructions; do not seed conclusions. Avoid subagent overhead for a small or single-lane diff.

## Review standard

- Find issues introduced by, or newly exposed through, the reviewed change. Label relevant pre-existing issues explicitly and do not make them blockers without a concrete reason.
- Prioritize correctness, authorization, privacy, security, data integrity, concurrency, failure handling, contracts, performance regressions, and meaningful test gaps.
- Treat performance and privacy findings as evidence-based: identify the changed path, realistic impact, and violated invariant. Do not recommend speculative optimization.
- Ignore formatting preferences and style points already enforced by tooling unless they cause a defect.
- Do not edit files, apply fixes, or expand product scope during the review.

## Findings

Every finding must include:

- severity and concise title;
- tight `file:line` location;
- concrete evidence and triggering scenario;
- user, security, privacy, data, or operational impact;
- a specific recommended fix consistent with existing architecture;
- focused verification for the fix.

Severity:

- `P0`: release-blocking data loss, critical security compromise, or irreversible corruption.
- `P1`: high-impact correctness, authorization, payment, privacy, or concurrency defect.
- `P2`: meaningful reliability, performance, contract, or UX regression.
- `P3`: low-impact but actionable defect; never a cosmetic preference.

Do not emit a finding when evidence, impact, or a feasible fix is missing.

## Output

Use this compact structure:

```text
Verdict: APPROVE | CHANGES REQUESTED | BLOCKED

Findings
[P1] Short title
Location: path/to/file:line
Evidence: ...
Impact: ...
Recommended fix: ...
Verify: ...

Validation
- command: result

Residual risks
- only unverified or out-of-scope risks
```

Sort findings by severity, then execution path. Deduplicate cross-lane findings. If none exist, state `No actionable findings.` and list only meaningful validation gaps.
