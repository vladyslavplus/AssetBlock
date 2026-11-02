# Code analysis service

Isolated FastAPI service that turns an uploaded archive into a real,
immutable, version-bound analysis envelope. It never approves, publishes,
admits, or stores product state: admission and selection live outside this
service. Product database, storage, and payment credentials are never
configured here.

## Boot

```powershell
$env:PYTHONPATH = "<checkout-root>\services\code-analysis;<checkout-root>"
$env:CODE_ANALYSIS_SECRET = "<rotated-secret-at-least-32-bytes>"
$env:CODE_ANALYSIS_NODE_EXE = "<node executable>"
$env:CODE_ANALYSIS_DOLOS_CLI = "<dolos dist cli.js>"
$env:CODE_ANALYSIS_SCANCODE_EXE = "<scancode executable>"
$env:CODE_ANALYSIS_MODEL_DIR = "<frozen encoder directory>"
$env:CODE_ANALYSIS_MODEL_MANIFEST = "<model file-hash manifest>"
$env:CODE_ANALYSIS_REFERENCE_MANIFEST = "<comparison snapshot manifest>"
$env:CODE_ANALYSIS_GALLERY_ROOT = "<snapshot gallery directory>"
# Optional: restricted read-only reference index DSN. Never the product DSN.
$env:CODE_ANALYSIS_INDEX_DSN = ""
# Optional: semicolon-separated site-packages directories for the worker.
# Apply dependency policy to every layer before use.
$env:CODE_ANALYSIS_WORKER_PATHS = ""
artifacts/code_analysis/env/service/Scripts/python.exe -m app.main
```

Tool versions are observed at startup from the configured executables and
reported per stage; hardcoded strings are never trusted. Defaults bind
loopback `127.0.0.1:8081`. A non-loopback bind is refused unless
`CODE_ANALYSIS_ALLOW_NON_LOOPBACK=1` is set, which requires externally
terminated TLS. Runtime secrets, machine-specific paths, and model artifacts stay outside Git.

## Contract

- `POST /internal/analysis`: streamed multipart `metadata` (JSON, 64 KiB)
  plus `archive` (zip, 100 MiB). Header `x-analysis-secret` is compared in
  constant time before any body byte is stored.
- `GET /internal/health`: static-safe liveness with policy digest, tool
  presence booleans, and the serving process ID.
- The request binds run, asset, version, archive hash, the exact
  declaration projection, declaration/material digests, policy digest,
  precise security evidence bindings, and the pinned reference/model
  identity into one immutable analysis digest. Execution reservation
  (`serviceKey/slotNumber/generation/leaseToken/hardDeadlineUtc`) is
  validated separately and never enters the digest, so a fresh lease for
  the same run keeps the same computational identity. The effective
  deadline is the earlier of the parsed reservation UTC deadline and the
  server limit; an expired reservation never starts.
- Reports carry schema `code-analysis-v1`, immutable `reportId`, stage
  evidence with observed tool identities, coverage with explicit omissions,
  bounded findings with an overflow summary, and a `sellerSafe` projection
  that contains no foreign private bytes, paths, names, vectors, or
  scores. `admittedBy` is always null here; the producer does not assert
  review eligibility or publication authority. The verifier mode is
  `DISABLED_BY_EVALUATION`. The immutable
  policy `code-analysis-policy-v1` binds its frozen evaluation evidence SHA-256
  and records `VERIFIER` as `NOT_APPLICABLE` with `POLICY_DISABLED`.
  Only the product server can issue an admission after validating its persisted
  production run and exact policy, security and stage bindings. Submission
  eligibility never publishes a version; an APPROVED human decision is required.
  Retained candidates are moderator evidence, not seller response obligations.
- Deployments must pin the policy key and canonical policy digest together
  and recompute the runtime-manifest digest from the complete approved manifest
  (including verifier mode and reason). Reuse requires the same exact policy
  digest. Missing configuration keeps claims closed. Development rows produced
  with a different development policy digest must be removed through a local
  development database reset before republishing this single initial policy.

## Budgets and safety

The single execution gate is claimed before any bulk body is read; busy
requests are rejected without buffering. Uploads stream to a private
workspace file (POSIX 0700 or restricted Windows ACL) with incremental
hashing, byte caps, and an upload deadline. The pipeline, including the
encoder, runs in a supervised child process inside our own managed run
containment: on Windows the worker starts suspended, is assigned to a
fresh kill-on-close Job Object, and only then resumes, so assignment
precedes the first executed instruction; the job handle itself is never
inheritable, and only stdin (read-only NUL), stdout, and stderr pass to
the child through an explicit handle allowlist. On POSIX every native
tool joins the supervisor-owned process group instead of forming new
sessions. Any containment failure refuses the run before any pipeline
code executes.
On timeout, disconnect, or task cancellation the whole containment is
stopped, worker and recorded native descendants plus group/job emptiness
are confirmed, and only then are plaintext removed and capacity released.
A worker that cannot be confirmed dead keeps the workspace and the gate.
Hosts that forbid our own job scope fail closed with an explicit BLOCKED
verdict; there is no degraded outer fallback.

Overall deadline 18 minutes from request start, token runner 3 minutes,
license runner 12 minutes, upload intake 5 minutes. Intake is bounded by
reservation, upload, and overall budgets; execution is bounded by
reservation and overall only, so the upload limit never caps a started run. Native runners use fixed argv, no shell, incremental output
caps with immediate tree stop on overflow, and bounded waits everywhere.
A startup and periodic sweeper removes abandoned workspaces older than
one hour and retries pending cleanups; new work is refused while a
cleanup is pending. Unconfirmed runs are fenced, never swept, and hold
the gate until operator restart. Logs carry run identifiers, stages,
durations, counts, and allowlisted error codes only — never exception
messages, tracebacks, request bodies, or DSNs.

## Moderator retrieval evidence

The single initial policy binds complete retrieval detail for each retained candidate: `queryFileId`, FILE query unit, explicit TOKEN/SEMANTIC/TOKEN_AND_SEMANTIC membership, nullable `tokenRank`/`tokenScore` and `semanticRank`/`semanticScore`, and a deterministic `material` flag. A zero token score is not a token observation and is excluded before ranking/counting; a real zero semantic score remains valid. Both channels are retained independently; the union never selects a winner or makes a moderation decision. Own excerpt byte/line coordinates identify the displayed query-file excerpt; they are not pair-verification match spans.

Token ranking uses descending score then ascending opaque file identity. Semantic ranking uses ascending exact distance then ascending file identity, aggregated across query windows; displayed similarity is clamped to [0,1], so equal displayed scores need not imply equal original distances. Union ordering is token order followed by unseen semantic candidates. Each channel retains at most its policy TOP (20); union budget is 40. The report summary records exact candidate, retained and overflow counts per query file and separate omissions caused by the global report budget. Omitted union count lies between the maximum and the sum of its omitted channel counts; total omitted candidates cannot exceed global finding overflow. Token overflow counts strictly positive observed bound pairs, not zero-score pairs or unobserved gallery scores. Semantic overflow counts indexed eligible gallery items excluded by TOP; full-gallery indexing is checked before querying. Missing historical scores are never reconstructed.

Material evidence means every retained token candidate tied at the highest strictly positive token score within its query file. Ranks remain unique ordinals (score descending, file ID ascending); ties do not discard material flags. Query summary records the measured rank-one score as tokenMaxScore before report truncation, or null when no positive token exists. This keeps material identity explicit even when rank one is omitted from the bounded report. There is no tuned numeric threshold, abstention or semantic-only material class. The frozen control reported ordinal per-query top-1 F1 0.843 versus semantic controls 0.51/0.59; raw pair scores were not persisted, so actual zero-score and raw-tie counts cannot be recovered. The control could select a zero-score ordinal winner and did not retain all raw-score ties. Its F1 justifies the token-channel top signal, not a measurement of the exact positive-guard/all-ties product rule. Pair verification remains disabled. The policy points to its existing `EVALUATION_EVIDENCE_SHA256` constant, owned by the frozen pair-verification evidence package. The material flag does not create a response request and does not change submission eligibility.

The product stores both typed score/rank columns and complete bounded JSON retrieval detail, plus report-level reference snapshot/revision, limits and overflow summary. Moderator case reads require a live Moderator role and deny the owner. Missing complete persisted detail is explicitly `SCORES_UNAVAILABLE`, never zero or an inferred rank. Seller projections expose neither private reference identity nor retrieval scores, ranks, membership or material flags. Publication still requires an explicit approved decision.
