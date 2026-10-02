---
name: review-change
description: Perform read-only AssetBlock plan, code, architecture, product, or project assessment. Use for requested reviews, audits, or validation; do not implement fixes or execute project verification.
---

# Review Change

Perform a read-only, evidence-based review. Report actionable defects and precise remedies without rewriting the implementation.

## Establish target

1. Read [repository instructions](../../../AGENTS.md).
2. Honor the requested target: PR, commit, range, files, plan, architecture, or project assessment. For an explicit plan/project review, use source and relevant documents even when Git has no diff. Distinguish existing capabilities, planned scope, and unsupported claims.
3. For a code-change review without an explicit target:
   - If the index or working tree has changes, review staged, unstaged, and relevant untracked files together.
   - If it is clean, compare the current branch with the merge-base of the locally available default branch. Do not fetch, pull, or mutate remotes merely to find a target.
   - If no reviewable change exists, report that limitation and request a target only when necessary to continue.
4. Inspect relevant surrounding code and tests, not only diff hunks. Do not broaden into an unsolicited whole-repository audit.

## Route lanes

- Backend changes: read `asblock-backend/AGENTS.md` and [backend review guidance](references/backend.md).
- Frontend changes: read `asblock-frontend/AGENTS.md` and [frontend review guidance](references/frontend.md).
- Root configuration, CI, or shared contract changes: load only the lanes they can affect.
- Cross-stack changes: review backend and frontend independently, then reconcile HTTP payloads, status/error handling, auth, caching, and user-visible behavior.
- Use independent backend/frontend subagents only when delegation is requested or the explicitly invoked workflow requires it. Otherwise inspect the two lanes separately in the same review. Give any reviewer the raw request, target, lane diff, and scoped instructions without seeding conclusions.

## Execution and evidence boundary

- Stay read-only. Do not run tests, builds, linters, formatters, restores, migrations, generators, package audits, applications, smoke tests, or other project verification commands. Read-only Git/file inspection is allowed; verification execution belongs to the implementer.
- Requested local agent-tool availability probes are allowed: Serena configuration/scoped symbols and Repomix `--version`/`--help`. Do not turn these probes into onboarding, full indexing, package generation, installation, or application verification.
- Reuse existing evidence with its command, result, and source-state provenance. Clearly label implementer-reported and historical results; never call an unrun check passing or an old result current without checking relevant source drift.
- Give focused verification for the implementer. Ask for broader suites/builds only when affected contracts, auth, payments, persistence, routing, configuration, or a critical browser journey justify them.
- Separate source approval from verification and release readiness. Missing runtime evidence is a stated gap; it is not automatically a source defect. Use `BLOCKED` only when unavailable evidence prevents a necessary review conclusion.

## Review standard

- For change reviews, find issues introduced by or newly exposed through the change. For an explicitly requested audit, assess existing issues within that scope. Label pre-existing conditions and do not make them change blockers without a concrete reason.
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

For code/plan reviews with actionable findings, return a short verdict (`CHANGES REQUESTED` or `BLOCKED`) followed by one copyable fenced prompt for the existing implementer. Begin the prompt with `# Context`; state the role, requested correction, scope, and existing authorization limits.

Put every finding inside that prompt, sorted by severity and execution path. Include priority, precise location, demonstrated trigger/evidence, impact, one chosen correction supported by the repository, expected behavior, and focused verification. Include reported-check limits, missing checks, and residual risks there. Do not duplicate findings in surrounding prose or add inline code-comment directives unless requested.

Use a longer outer fence when the prompt contains fenced commands. Do not imply authorization for packages, migrations, commits, deployments, or product expansion that the user has not granted.

If no actionable findings exist, return `APPROVE` with `No actionable findings.` and brief verification limits; qualify source-only approval explicitly. If essential review evidence is unavailable, use `BLOCKED` and identify what is missing without manufacturing defects.

For broad product/architecture assessments, answer the requested questions directly and distinguish observations, recommendations, and readiness gaps. Include a fix prompt only for concrete actionable defects; do not turn future product ideas into mandatory findings.
