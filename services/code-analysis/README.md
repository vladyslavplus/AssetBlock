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
  scores. `admittedBy` is always null here; `canAuthorizePublication` is
  false and the verifier mode is `PENDING_RESEARCH_EVALUATION`.

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

