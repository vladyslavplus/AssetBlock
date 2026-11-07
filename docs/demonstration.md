# AssetBlock demonstration

This document describes a local, repeatable product demonstration and a bounded research explanation. It is a
runbook and an evidence record for the candidate. It is not a release statement.

Status labels used below: **VERIFIED** means the source or output was read in this change. **TEST-VERIFIED**
means a named existing test selects the behaviour; the test was listed or run as stated. **SIMULATED** means
the browser stub stands in for the server. **GAP** means the behaviour is not shown by any lane here.
**NOT VERIFIED** means it was not checked in this change. **UNKNOWN** means the source or package does not record
it. **NOT MEASURED** means no recorded value exists for it, so it is never a zero. **NOT RECORDED** means the
evidence states that the value was not recorded. **OBSERVED IN RECORDED RUN** means the value appears in a
recorded run record and was not re-checked here. **SETUP UNAVAILABLE** means the scenario's live step cannot be
run in this candidate.

## 1. Purpose and non-claims

Purpose:

- Show nine workflow scenarios (D1 to D9) on the product: upload, declaration, analysis findings, seller
  responses, Moderator review, decisions, pending versions, stale-basis conflicts, self-review denial and
  private-evidence protection.
- Show how the research results are identified, compared and reproduced from recorded evidence.
- State the limits of both parts.

Non-claims:

- No automatic publication. Passed checks and READY status never publish a version.
- No model quality acceptance. The research outcome is "held-out evaluation not performed; corpus not admitted".
  Every numeric result here is validation-only and selection-contaminated.
- No originality, theft, legal or universal similarity verdict. A similarity finding is evidence for review.
- No production readiness, deployment, payment, cross-browser, screen-reader or large-scale claim.
- The product verifier and its policy remain disabled. Nothing in this demonstration changes them.
- The two trained checkpoints are not approved for product use (see section 7.2).
- No scenario is accepted by a runner status. The runner status LANES_PASS means every selected lane passed. It is
  not scenario acceptance (see section 5).
- No scenario was run against a live analysis host. Lanes run test code; they do not operate the live system.

## 2. Environment prerequisites

| Item | Requirement | Source | Status |
| --- | --- | --- | --- |
| Operating system | Windows with PowerShell 7 | `PowerShell` tool use in this change | VERIFIED for the commands listed here |
| .NET SDK | 10.0.100 or later in the 10.0 feature band | `asblock-backend/global.json` (`rollForward: latestFeature`) | VERIFIED; 10.0.401 observed |
| Node.js | Version compatible with Next.js and Playwright | Root and `asblock-frontend/package.json` | VERIFIED; v24.20.0 observed. Not pinned: neither `package.json` declares `engines` |
| pnpm | 11.13.0 | `asblock-frontend/package.json` (`packageManager`) | VERIFIED |
| Browser for Playwright | Chromium installed for Playwright | `README.md` | OBSERVED IN RECORDED RUN (browser-stub lanes passed in the recorded run); not re-checked here |
| Docker Desktop with Compose | Required for PostgreSQL, ClamAV and other Testcontainers lanes and for the local Compose services | `asblock-backend/docker-compose.yml`; Testcontainers lanes | OBSERVED IN RECORDED RUN (Docker server 29.9.2 at the runner preflight; HTTP and store lanes started); not re-checked here |
| Python interpreters | Pinned CPU interpreter `artifacts/code_analysis/env/ml`, service interpreter `artifacts/code_analysis/env/service`, CUDA encoder interpreter `artifacts/feasibility_pilot/env/ml` | Paths named in the verification record and in `scripts/model_evaluation/README.md` | The three paths exist. The CPU interpreter reports Python 3.12.10 (observed in this change). The service and CUDA interpreter versions were not checked |

Compose services, as defined in `asblock-backend/docker-compose.yml`:

| Compose service | Image family | Host port setting (key name) | Notes |
| --- | --- | --- | --- |
| `postgres` | `pgvector/pgvector` PostgreSQL 16 | `POSTGRES_PORT` (default 5432) | Required by the API |
| `seaweedfs` | SeaweedFS S3 | `SEAWEEDFS_S3_PORT` (default 8333) | Default object storage |
| `redis` | Valkey (Redis protocol) | `REDIS_PORT` (default 6379) | The service name is `redis`, not `valkey` |
| `clamav` | ClamAV | `CLAMAV_PORT` (default 3310) | Needs its environment file `clamav/clamd.env` |
| `mailpit` | Mailpit | `MAILPIT_SMTP_PORT`, `MAILPIT_UI_PORT` | Local email catcher only |
| `minio` | MinIO | profile `minio` only | Not started by default |
| `aspire-dashboard` | .NET Aspire dashboard | fixed local ports | Optional observability |

Compose start for the services this demonstration uses (run from the repository root; no volume removal):

```text
docker compose -f asblock-backend/docker-compose.yml up -d postgres seaweedfs redis clamav mailpit
```

Never run `docker compose down -v`. The Compose header says a volume created before the single `Initial`
migration must be recreated by an intentional decision, and that wiping volumes discards local data.

Secrets. The following configuration keys hold secret values. Only their names appear here. Values come from
.NET User Secrets or from the operator's configured environment; they are never written into tracked files:

- Backend (`ConnectionStrings:DefaultConnection`, `ConnectionStrings:Redis`, `Jwt:Key`, `Stripe:SecretKey`,
  `Stripe:WebhookSecret`, `SeaweedFs:SecretKey`, `Minio:SecretKey`, `Encryption:Keys` entries,
  `AnalyticsRateLimiting:BffSigningSecret`, `Email:Smtp:Password`, `Ai:OpenRouter:ApiKey` when the AI
  provider is enabled, and `CodeAnalysis:ServiceSecret` when the analysis host is enabled). Key names verified in
  `asblock-backend/AssetBlock.WebApi/appsettings.Development.json` and `CodeAnalysisOptions.cs`.
- Code-analysis service: `CODE_ANALYSIS_SECRET` (at least 32 bytes). Verified in `services/code-analysis/app/main.py`.
- Frontend server: `ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET` (name verified in `asblock-frontend`).

## 3. Launch order

Status: Not reproducible without owner-provided configuration. Launch recipe checked against source only; not executed. A live submission needs the analysis keys, tool
paths and provisioned accounts; without them the host denies submission. The repeatable path in this candidate is
the lane runner in section 5.

Confirm that the required ports are free and that no process you do not own is using them. Start each process
hidden, with separate output files, and keep its process ID. For the API, record the ID of the process listening on port 5088: `dotnet run` can start a child process, and the launcher's ID alone is not enough. Do not kill processes by name.

### 3.0 Restore and install prerequisites

- .NET: `dotnet restore asblock-backend.slnx`, then `dotnet build asblock-backend.slnx`, from `asblock-backend/`
  (command shapes from `asblock-backend/README.md`). The integration lanes pass `--no-restore`, so restore first.
- Frontend: `pnpm install --frozen-lockfile` in `asblock-frontend/` (command shape from `README.md`), then the
  production build in section 3.4.
- Playwright Chromium (command shape only; not run in this change), from `asblock-frontend/`:
  `node node_modules/@playwright/test/cli.js install chromium`.
- Service: the interpreter `artifacts/code_analysis/env/service/Scripts/python.exe` is named in
  `services/code-analysis/README.md`. Its packages were not installed or checked in this change.
- Backend secrets: `dotnet user-secrets set "<key>" "<value>" --project AssetBlock.WebApi` for each key in section 2.

### 3.1 Compose services

See section 2. Wait until each service reports healthy in `docker compose ps`. The `clamav` service reads its environment through `env_file: ./clamav/clamd.env` in
`asblock-backend/docker-compose.yml`; that file is tracked in the repository.
The test lanes do not use these Compose services. Their own Testcontainers are: PostgreSQL (`IntegrationTestFixture` and
`PostgresFixture`, used by every selected lane); ClamAV in `SafetyUploadHttpIntegrationTests` (lane `d4-upload-safety-http`).
Redis (`RedisAnalyticsDistributedRateLimitIntegrationTests`), Mailpit (`SmtpEmailSenderMailpitTests`), ClamAV
(`ClamAvContentMalwareScannerContainerTests`) and object storage (`AssetStorageContractTests`) also start containers, but
no lane selects those classes.

### 3.2 Code-analysis service

Working directory: repository root. The service package lives under `services/code-analysis`, so `PYTHONPATH`
must include that directory and the checkout root (verified in `services/code-analysis/README.md`).

```text
Executable: artifacts/code_analysis/env/service/Scripts/python.exe
Arguments:  -B -m app.main
Environment (names and purpose only; values are never written here):
  PYTHONPATH=services/code-analysis;.
  CODE_ANALYSIS_SECRET          shared secret, at least 32 bytes (required; start fails without it)
  CODE_ANALYSIS_NODE_EXE        node executable used by the analysis tools (unset means no path)
  CODE_ANALYSIS_DOLOS_CLI       dolos CLI script path (unset means no path)
  CODE_ANALYSIS_SCANCODE_EXE    ScanCode executable (unset means no path)
  CODE_ANALYSIS_MODEL_DIR       frozen encoder directory (unset means no path)
  CODE_ANALYSIS_MODEL_MANIFEST  model file-hash manifest (unset means no path)
  CODE_ANALYSIS_REFERENCE_MANIFEST  comparison snapshot manifest (unset means no path)
  CODE_ANALYSIS_GALLERY_ROOT    snapshot gallery directory (unset means no path)
  CODE_ANALYSIS_INDEX_DSN       optional read-only reference index connection; never the product connection
  CODE_ANALYSIS_WORKER_PATHS    optional semicolon-separated site-packages directories for the worker
  CODE_ANALYSIS_BIND=127.0.0.1  default; non-loopback needs CODE_ANALYSIS_ALLOW_NON_LOOPBACK=1 and TLS termination
  CODE_ANALYSIS_PORT=8081       default in app/main.py
```

`app/main.py` reads each optional path with `_optional_path`: an unset variable gives no path, and the service still
starts. The service README describes the health report as showing tool presence, so an absent tool is visible there
rather than stopping the process. Check the health report before a run. Tool versions are observed at start-up, not
taken from configuration.

Readiness: `GET http://127.0.0.1:8081/internal/health` (verified in `services/code-analysis/app/service.py`). It
reports liveness, policy and tool presence only. It does not prove that a report can be produced.

### 3.3 API

Working directory: `asblock-backend`. The `http` launch profile listens on `http://localhost:5088` (verified in
`asblock-backend/AssetBlock.WebApi/Properties/launchSettings.json`). Build first, then run without restore.

```text
Executable: dotnet
Arguments:  run --no-build --no-restore --project AssetBlock.WebApi --launch-profile http
```

Migrations: the API runs `DatabaseMigrationService` at start. It applies pending migrations when `Database:AutoMigrate`
is true. `appsettings.json` sets it to false and `appsettings.Development.json` sets it to true, so a Development start
migrates the database and creates a dev admin account when none exists.

Analysis configuration (names and purpose only; values come from user secrets or the environment, never from this
document). `CodeAnalysis:Enabled` defaults to `false`, and the committed appsettings files contain no `CodeAnalysis`
section (`CodeAnalysisOptions.cs`):

| Key | Purpose |
| --- | --- |
| `CodeAnalysis:Enabled` | Turns the analysis host on. Default `false`, which denies submission |
| `CodeAnalysis:BaseUrl` | Base URL of the service in section 3.2 |
| `CodeAnalysis:ServiceSecret` | Shared secret sent in the request header named by `CodeAnalysis:AuthenticationHeader` (default `X-Analysis-Secret`) |
| `CodeAnalysis:PolicyDigest`, `CodeAnalysis:RuntimeManifestDigest` | Digests the backend checks against the analysis policy identity (see the comments in `CodeAnalysisOptions.cs`) |
| `CodeAnalysis:PolicyManifest:*` | Policy manifest fields: `PolicyKey`, `PolicyDigest`, `SchemaName`, `StageMatrixJson`, `VerifierMode`, `VerifierReason`, `ToolIdentitiesJson`, `LimitsJson`, `SecurityValidityJson`, and optional `ModelKey`, `IndexKey`, `ReferenceSnapshotId` |

Readiness: `GET http://localhost:5088/health/ready` must return success. `503` is a failure. `/health/live` is
liveness only (both routes verified in `asblock-backend/AssetBlock.WebApi/Extensions/HealthCheckExtensions.cs`).
A successful readiness result does not show that analysis is enabled. Check the analysis keys above before a
submission is attempted.

### 3.4 Frontend

Working directory: `asblock-frontend`. Use a production build for this candidate. Do not reuse a stale `.next`
output, and do not use the stub `.next-e2e` output used by Playwright. Before the build, remove a stale `.next` output (document
only; delete it only after the containment check in section 10).

```text
Build:      node node_modules/next/dist/bin/next build
Executable: node
Arguments:  node_modules/next/dist/bin/next start --hostname 127.0.0.1 --port 3000
Environment:
  NEXT_PUBLIC_API_BASE_URL=<approved local API base, for example http://localhost:5088>
  ASSETBLOCK_API_BASE_URL=<same approved local API base, server side>
  ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET=<owner-provided value; name read by app/api/analytics/events/route.ts; never written here>
```

Verified: both key names are read by the frontend (`process.env.NEXT_PUBLIC_API_BASE_URL` and
`process.env.ASSETBLOCK_API_BASE_URL`). Port 3000 is the port used by the plan; `next start` has no default
port in `asblock-frontend/package.json`. Secrets never go into `NEXT_PUBLIC_` variables.

Readiness: the home page responds, and one authenticated BFF read succeeds. The authenticated read needs an account:
a Development start creates a dev admin when none exists (section 3.3). Section 4 lists the roles the scenarios use;
this runbook creates no other account.

### 3.5 Stop order

Stop the frontend, then the API, then the service, by the process IDs recorded at launch. Then stop the Compose
services that were started for this run (`docker compose stop <services>`). Verify that the ports are free.

## 4. Account and role map

The scenarios need these actors. Separate accounts show authorization separation. They do not represent
independent people.

| Actor | Used in | How it is provisioned | Status |
| --- | --- | --- | --- |
| Seller A (owner of the demonstration asset) | D1 to D7, D9 | Operator-provisioned account | UNKNOWN: no tracked script in this change creates it |
| Seller B (unrelated seller) | D8 and cross-seller checks in D9 | Operator-provisioned account | UNKNOWN |
| Moderator M1 | D1, D2, D3, D5, D7, D9 | Role granted by Admin with the role-revision check | Role rule per the access map in `docs/agent-context.md` (controller not re-read here); account creation UNKNOWN |
| Moderator M2 | Second reviewer (D7 and D8 comparison) | As M1 | UNKNOWN |
| Owner who also has the Moderator role | D8 | Seller A granted Moderator role | Role rule per the same access map; account setup UNKNOWN |
| Buyer with an isolated test entitlement | D6 | Operator-provisioned purchase | UNKNOWN |
| Anonymous viewer | D1, D6, D9 | No account | Source-verified public surface behaviour |

Admin can provision User and Moderator roles, but Admin gains no Moderator access (per the access-map row of
`docs/agent-context.md`; the controller source was not re-read in this change). The integration tests create their
own users inside their containers. The demonstration accounts are not created by any tracked script in this change.
Passwords and tokens are never written into this runbook or into captures.

`Database.SeedDemoAssets` appears as a development configuration key in `appsettings.Development.json`. Its
effect on accounts was NOT VERIFIED.

## 5. Scenario table

List the lanes and validate the manifest with the same interpreter; no run directory is created:

```text
artifacts/code_analysis/env/ml/Scripts/python.exe -B -m scripts.demonstration.run_scenarios list
artifacts/code_analysis/env/ml/Scripts/python.exe -B -m scripts.demonstration.run_scenarios check
```

Run one scenario:

```text
artifacts/code_analysis/env/ml/Scripts/python.exe -B -m scripts.demonstration.run_scenarios run --scenario D1 --run-id <new-run-id>
```

Run every default lane of every scenario: `run --all --run-id <new-run-id>`. Optional lanes run only with
`--include-optional` or when named with `--lane`.

A run id matches `[A-Za-z0-9][A-Za-z0-9_-]{0,79}`. The runner refuses a run id that already exists, an unknown lane id, a lane outside the selected scenarios, and an
empty selection. Results are written to `artifacts/demonstration/runs/<run-id>/result.json`, with one log pair per
lane. The lane list and minimum counts are in `scripts/demonstration/scenarios.json`. Counts below were discovered
with `run_scenarios discover` (dotnet `--list-tests`, Playwright `--list`, Vitest). They are not results of a run.

Exit codes:

| Exit | Run status | Meaning |
| --- | --- | --- |
| 0 | LANES_PASS | Every selected lane passed with counts at or above its minimum |
| 1 | FAIL | A lane failed or could not run, or discovery found a failed probe or an unparsed count |
| 2 | UNKNOWN | A lane exited 0 but its test counts were not parseable |
| 3 | refused | Unknown scenario or lane id, lane outside the selected scenarios, empty selection, or invalid or reused run id. No run directory is created |
| 4 | PARTIAL | No lane failed, but a default lane of a selected scenario was not selected |

LANES_PASS means every selected lane passed. It is not scenario acceptance. D2 and D3 live match: SETUP UNAVAILABLE
(see the notes under the table). A scenario status of PASS means every default lane of that scenario ran and passed.

Lane evidence kinds:

- **http-postgres-test-host**: in-process HTTP tests (`WebApplicationFactory`) against a PostgreSQL test database
  in a container. Object storage is replaced by a test double and analysis is disabled. Docker needed.
- **postgres-store**: store-level tests in the infrastructure test project against a PostgreSQL test database.
  Docker needed.
- **browser-stub**: Playwright against the local API stub. Simulated.
- **unit**: in-process tests (xUnit or Vitest).

The optional live lane `d6-live-pending-v2-optional` uses `http-postgres-test-host` only for its preflight; that lane is
opt-in and uses real services when configured.

No lane consumes the fixtures in section 6. They are inputs for a manual live run, which was not performed.

Container preflight. Before any lane of kind http-postgres-test-host or postgres-store starts, the runner runs
`docker version --format {{.Server.Version}}` once per run, as a direct executable, hidden, with a short timeout. If
that fails, every such lane is recorded as FAIL with reason `container runtime unavailable: <reason>` and is not
started. Unit and browser-stub lanes still run. A run that selects no such lane does not probe. Covered by
`scripts/demonstration/tests/test_runner.py` (`ContainerPreflightTests`, with a monkeypatched probe; no real daemon
failure was observed).

Per-row evidence: each run records the run id, git HEAD and dirty flag, the manifest SHA-256, lane ids, argument
vectors, exit codes, counts, durations and log paths in `result.json`. The runner does not record actor identities;
the run notes must. Screenshots, if any, are taken only after server evidence exists and must not show private source
or Moderator rationale.

| ID | Title | Actors | Intended live steps (not run by the lanes) | What the lanes actually do | Lanes (kind, discovered count) | Gaps |
| --- | --- | --- | --- | --- | --- | --- |
| D1 | Clean submission requires approval | Seller A, M1, anonymous, buyer | Upload the clean-original fixture; declaration; required checks; explicit submit; M1 approves; anonymous and buyer surfaces read over HTTP before and after approval | HTTP lane seeds READY versions and submissions directly into the test database and seeds bytes through the test storage replacement, then reads public surfaces over HTTP. It does not upload, declare, check or submit. The latest-not-public matrix checks the expected status per actor and route when the newest version is not public and an earlier version is approved. The never-approved first-version HTTP test seeds a READY-only first version beside an approved control. It checks that the first version is absent from catalog search, detail, version list, asset download, version download and checkout, with positive controls on search, detail and version list. Non-purchaser download 403s come from the purchase check; the 404 detail and checkout and the empty version list carry the publication gate. A store lane asserts that a no-findings decision still requires an explicit approval record. Browser lane drives the frontend against the stub | `d1-publication-http` (http-postgres-test-host, 10); `d1-latest-not-public-http` (http-postgres-test-host, 6); `d1-never-approved-first-version-http` (http-postgres-test-host, 1); `d1-no-findings-postgres` (postgres-store, 1); `d1-browser-approval` (browser-stub, 1) | No lane consumes the clean-original fixture. No server upload, declaration, check or submit. Browser lane simulated. Complete direct, bundle, list and search matrix not covered |
| D2 | Declared third-party match | Seller A, M1 | Upload the declared-third-party fixture; analysis reports a match; seller answers the required finding; M1 attempts the decision | HTTP lane seeds outstanding seller responses and asserts the decision-specific conflict. Store lane asserts that an unresolved material finding blocks approval with no decision record and no publication snapshot. Browser lane drives the seller answer flow against the stub. No lane produces a match or consumes the fixture | `d2-decision-blocked-by-responses` (http-postgres-test-host, 1); `d2-unresolved-finding-postgres` (postgres-store, 2); `d2-browser-answer-before-submit` (browser-stub, 1) | Live match SETUP UNAVAILABLE (needs enabled analysis host and populated reference index). Answering a finding does not close it by itself: no lane asserts that an answered finding stays open. Attribution and distribution terms unverified. Missing rights and notices covered by server validation not run here |
| D3 | Independent implementation match | M1, Seller A | Two authored implementations (independent-pair-a and -b) reviewed by M1, who records a bounded disposition; invalid dispositions rejected | Unit lane checks disposition validation (32 tests). A Vitest unit test checks that outstanding seller responses keep the decision blocked. Browser lane drives a disposition and approval against the stub. No lane produces a match or consumes the fixtures | `d3-disposition-validation` (unit, 32); `d3-case-workspace-unit` (unit, 1 selected of 34 in file); `d3-browser-disposition` (browser-stub, 1) | Live similarity match SETUP UNAVAILABLE (needs enabled analysis host and populated reference index). Row is test-verified or simulated only. Independence of the pair is structural only. Quality of reasoning is human review |
| D4 | Failed safety check | Seller A | Upload the unsafe-archive fixture (generated at build time) and a runtime test payload; server rejects both; submit denied under a host with analysis unavailable | HTTP lanes build their own traversal archive in the test (not the unsafe-archive fixture) and a runtime-generated test string, and assert ARCHIVE_PATH_TRAVERSAL and MALWARE_DETECTED with blocked actions and no downstream jobs. The submission test asserts ERR_ANALYSIS_NOT_AVAILABLE on a host with analysis disabled | `d4-upload-safety-http` (http-postgres-test-host, 2) | Submit denial is host configuration, not an isolated safety override. Only one ../ ZIP and one test-string ZIP reach HTTP; no archive bomb, special tar, empty or oversized archive case. No enabled-analysis host, Moderator action or quarantine route. Object storage is a test replacement. Malware case uses a runtime test string, not a sample |
| D5 | Corrected resubmission | Seller A, M1 | Changes requested; seller corrects the declaration or bytes (corrected-resubmission fixture) and resubmits; a byte-changing correction gets a new version and fresh checks; the older decision stays in history | Browser lane simulates a declaration-only correction on the stub: the seller saves the declaration and resubmits, and the test asserts that no archive upload request is made. Store lane seeds a later submission on the same asset version after a changes-requested decision and asserts the older decision is hidden from the current view and kept in history. Store lane shows an explicit linked evidence resubmission on the same version, followed by an unchanged withdrawal and resubmission. No lane changes bytes | `d5-browser-resubmit` (browser-stub, 1); `d5-finalizer-resubmission` (postgres-store, 4); `d5-linked-evidence-resubmission-postgres` (postgres-store, 3) | No byte-changing correction, no new-version re-scan, and no refusal of an old report for changed content. Browser correction is declaration-only and runs against the stub. Version-list equality in the browser journey cannot fail and is not claimed. Full immutable explanation, expiry and withdrawal path not established |
| D6 | Pending version 2 beside approved version 1 | Seller A, M1, buyer, anonymous | Approve version 1; buyer purchases version 1; upload and submit version 2; request changes; approve corrected version 2 | Seven HTTP tests seed approved and pending versions directly and assert public, buyer and decision-route behaviour: version 1 stays public; pending version 2 is hidden on catalog, detail and version routes; buyer pending download returns 404; approving version 2 promotes it. Browser lane drives the stub | `d6-public-surface-http` (http-postgres-test-host, 7); `d6-browser-beside-pending` (browser-stub, 1); `d6-live-pending-v2-optional` (optional, not run) | Live lane opt-in and skipped by default; a skip is not a pass. Pending rows are seeded, not produced by upload and submit. Embeddings, search, tags, bundle, license and price matrix not covered. Stale-empty catalog search after approval is bounded eventual consistency (outbox dispatch or two-minute cache TTL) |
| D7 | Stale-review rejection | Seller A, M1, M2 | M1 opens the case and keeps its basis; seller changes evidence; M1 decides on the old basis | HTTP tests assert standard conflicts for a stale basis and for a repeated decision, with no decision record. Store lane asserts that a changed tuple member (content, report, declaration, material, evidence, policy or evidence digest; seven cases) is refused with no decision record and no publication snapshot; a changed policy returns BLOCKED and the other six return STALE. Store lane asserts the production outcome when a tuple member changes while the case is claimed (declaration, evidence, response, security recheck). The race test asserts one of two consistent end states; which branch runs is not deterministic. Browser lane simulated | `d7-http-conflicts` (http-postgres-test-host, 2); `d7-race-evidence-edit` (http-postgres-test-host, 1); `d7-tuple-change-postgres` (postgres-store, 7); `d7-tuple-workflows-postgres` (postgres-store, 4); `d7-browser-stale-rationale` (browser-stub, 1) | Race test branch is not deterministic. HTTP stale test asserts no decision; the no-publication assertion is in the store lane. A superseded case may return NOT_ACTIVE rather than STALE; tests assert their exact code. Zero-effect checks cover asserted tables only |
| D8 | Self-approval prevention | Owner with Moderator role, M1 | Owner tries claim, case read, evidence read, queue read and decision on own asset; a separate Moderator is unaffected | HTTP matrix tests call the claim, decision, case, case-evidence and queue routes for each caller class (owner, owner with the Moderator role, stranger, Admin without the Moderator role, revoked Moderator, other Moderator). Admin denials assert Forbidden. The owner-Moderator queue step returns 200 and excludes the own case. The owner-Moderator case-evidence step asserts Forbidden and one new audit row, the only selected step that expects one. Each denial step compares the audit-row delta with its expected value. A decision test asserts no effect when the decider owns the case or the case is unclaimed. Browser lane drives the owner-Moderator attempt on the stub | `d8-http-role-ownership` (http-postgres-test-host, 6); `d8-browser-same-account` (browser-stub, 1) | Admin without the Moderator role gets Forbidden in the selected matrix tests; no Admin-only case list is tested outside those matrices. Audit-row expectations are per step, not a general audit-completeness check. Browser lane simulated; hidden-button state is not server evidence |
| D9 | Private-evidence protection | Seller A, M1, anonymous, other seller | Restricted own-excerpt evidence: synthetic canaries; other actors try access; M1 reads with audit; a failed audit is checked | HTTP tests read the restricted own-excerpt evidence through the Moderator route and assert the audit is written before the body, a failed audit releases no text, responses are private and no-store, and five denial cases hold. A canary test checks a synthetic sentinel across cross-seller responses (one 404 on the cross-seller check) | `d9-restricted-evidence-http` (http-postgres-test-host, 10); `d9-canary-cross-seller` (http-postgres-test-host, 1) | No foreign-comparison evidence kind and no buyer read in these lanes. No browser lane (screenshots cannot prove sinks). Buyer body, BFF, cache, dispatched notification payloads and structured logs not reached. No whole-flow privacy claim |

Notes under the table:

- Discovered counts were produced by `run_scenarios discover --all` on manifest `f1cb0461...` (exit 0); every lane's
  discovered count equals its manifest minimum. The optional live lane is not selected by discovery.
- `d3-case-workspace-unit` reports 34 tests in its file; the `-t` name filter selects 1 and vitest reports the other 33
  as filtered-out skips.
- SETUP UNAVAILABLE (D2 and D3 live match): the live step needs an enabled analysis host and a populated reference
  index. This candidate did not have them, and no lane stands in for that step.
- Discovery (`run_scenarios discover`) runs each lane's probe and is not a run. It writes only its logs under
  `artifacts/demonstration/discover/`.

## 6. Fixtures

Fixture sources are authored under `scripts/demonstration/fixtures/`. `NOTICE` states that they are authored
for the demonstration under a public-domain style dedication, and that the stand-in third-party component is
MIT-licensed by its authored license text and has no upstream project.

| Fixture | Purpose | Scenario | Intended state, not observed by any lane |
| --- | --- | --- | --- |
| `clean-original` | Clean base library | D1, D5, D6, D7, D8, D9 | Archive and malware checks pass; no finding is promised |
| `declared-third-party` | Base library plus a stand-in MIT component with notices and changes | D2 | Declaration needs matching notice locations; a match needs a live analysis host, which this demonstration does not run |
| `independent-pair-a`, `independent-pair-b` | Two separately written moving-average implementations | D3 | Similarity is possible only with a live reference index; not promised |
| `corrected-resubmission` | Byte-changing correction of the base library (even-length median) | D5, D6 | New version; checks run again; approval explicit |
| unsafe archive | Path-traversal entry, generated at build time | D4 | Rejected with ARCHIVE_PATH_TRAVERSAL |

No lane consumes these fixtures. The manifest names them under `live_run_fixtures`. They are inputs for a manual live
run, which was not performed.

Build the fixtures into an ignored scratch directory:

```text
artifacts/code_analysis/env/ml/Scripts/python.exe -B -m scripts.demonstration.fixtures build --out artifacts/demonstration/fixtures
artifacts/code_analysis/env/ml/Scripts/python.exe -B -m scripts.demonstration.fixtures verify --dir artifacts/demonstration/fixtures
```

The builder writes deterministic ZIP archives (sorted entries, fixed 1980-01-01 timestamps, fixed permissions,
LF line endings), `declaration.json` (example seller declaration with the field names of the seller payload) and
`fixture-manifest.json` (SHA-256 and size of each archive and entry, purpose, rights and provenance). Building
twice on the same interpreter gives identical hashes. To repeat the check, build into a second directory
(`--out artifacts/demonstration/fixtures-repeat`) and compare the two `fixture-manifest.json` files byte for byte.
The per-entry hashes in `fixture-manifest.json` are the stable
identity of each fixture. Archive-level bytes depend on the compression library build, so archive hashes are
recorded with the interpreter version in the local run ledger, not in this document. Fixtures are illustrative only
and must never be used for research training, evaluation or threshold tuning.

Unsafe-archive note: the archive is generated at build time and is not stored in the repository. The malware case
uses a standard test string generated at runtime by the test helper. No real malware and no seller code are
executed.

## 7. Research

### 7.1 Scope and source boundary

This section copies recorded identities and metrics from two evidence packages and one evaluation evidence file. It
does not re-score, retune or reload any model.

- Retrieval package: `artifacts/code_retrieval/runs/retrieval-evidence-20261030-03/evidence-package.json`.
  SHA-256 `44ce724068485e9e5304304d8b3ee276c3248aa6e09e879cbd2b47eb0574cc9b` (MATCH against the recorded value).
  Schema `code-retrieval-evidence-package-v1`, outcome SUCCEEDED, `CanAuthorizePublication` false,
  `SecurityEvidence` NOT_RUN.
- Verifier package: `artifacts/pair_verification/runs/pairs-evidence-20261031-03/evidence-package.json`.
  SHA-256 `676ad4da5a40ea7a6f040b0c51cc060088689cfd26f23d4a3974b965f1063973` (MATCH). Schema
  `pair-verification-evidence-package-v1`, outcome SUCCEEDED.
- Evaluation evidence file for the retrieval comparison:
  `artifacts/code_retrieval/runs/retrieval-eval-20261030-02/evaluation-evidence.json`. Its SHA-256 is
  `43493ea87d49f5c86c87b1de9eb58c1cf5994aba89ca95addd8e46866c90468e`. The retrieval package records the same value
  under `identities.evaluationEvidenceSha256`, and its `chain.evaluation` names run `retrieval-eval-20261030-02`.
  Link: VERIFIED in this change.

Both packages record `redistribution` as denied or local-only. Checkpoints, vectors and source text stay under
`artifacts/`. Nothing in these packages is exported. Neither package was used for training, tuning or scoring here.

Access status: NOT VERIFIED in this candidate. The checkpoint files were not located, statted, loaded or hashed. The
owner must confirm availability before any reproduction (custodian row, section 7.2).

### 7.2 Model identity records

| Field | Model 1: candidate retrieval (fine-tuned encoder, label E3) | Model 2: pair verifier |
| --- | --- | --- |
| Role | Fine-tuned candidate retrieval encoder. Product retrieval remains the frozen baseline. | Pair verifier. Product verifier disabled. |
| Architecture | VERIFIED from source; not a package field. Encoder `RobertaModel` with `RobertaTokenizerFast`, loaded from safetensors with `local_files_only` (`scripts/code_retrieval/checkpoint.py`). Hidden size 768 (`scripts/code_retrieval/index_verify.py`, `EMBEDDING_DIMENSIONS`). Pooling: padding-masked mean over all non-padding tokens including framing, FP32, then L2 normalisation (`scripts/code_retrieval/retrieval_models.py`, `POOLING`). Last 2 encoder blocks trainable (`scripts/code_retrieval/config.py`, `trainableEncoderBlocks`) | VERIFIED from source; not a package field. Joint verifier `JointVerifier` (`scripts/pair_verification/architecture.py`): encoder plus one linear head with two outputs over the first token (position 0) of the last hidden state. Encoder: UniXcoder base-nine, as the experiment record names it (`scripts/pair_verification/experiment.py`); the encoder class is `RobertaModel` (`scripts/pair_verification/checkpoints.py`). Pair layout string `CLS encoder-only SEP A SEP B SEP` with 5 framing tokens, 507 content tokens, maximum 512 tokens, side budgets A 254 and B 253 (`experiment.py`). Last 4 blocks trainable: `freeze_except_last_blocks(model, 4)` (`scripts/pair_verification/training.py`) |
| Base revision | `identities.trustedModelRevision` `5f0dc256eb0904af496d427451da5d7f90261676` (recorded as the trusted base revision; also `upstreamModelRevision` in `scripts/code_retrieval/config.py`) | Same trusted base revision `5f0dc256eb0904af496d427451da5d7f90261676` (frozen base encoder) |
| Selected epoch or step | Label E3 is the third trained epoch. Steps 84 planned and 84 successful. Selection is CHOSEN_BY_RULE with approval false. The package has no single selected-epoch field for this model: UNKNOWN as a field | `selectedEpoch` = 2. Steps planned and successful: UNKNOWN in this package (no step-count key at `measurements.training.stepsPlanned` or `measurements.training.stepsSuccessful`) |
| Training-config identity | `identities.configSha256` `9734956aa66e8b27556a0b8d244823a9739d2338eac1950ffe85d06992981695`; `identities.configByteSha256` `5a7f88c939ce350444aaca4dc945c61dcd942d14afc00a1fe06d3671337ac4e9` | `identities.experimentConfigSha256` `616d253c7e5d9026448e83939806856c288f2a965b9e409bddfd8d9387f409e2` |
| Training and data purpose identity | Run `retrieval-train-20261030-02`. Prepare run recorded as the selected-set prepare run ending `20261030-01`, `prepareDataSha256` `47d8c4cc0a72ec34260df19acde511200309b2ff2172b66c0a1088b7ce4ff68c`, 111 admitted triplets, `trainingAdmissionSha256` `b16c8ab156f7257b09620dfa4b027b6b28251763e606d7c73a0cb8ff2d9896e8` | Run `pairs-train-20261030-03`. `trainingPairsSha256` `6b932501a53d3a09fe797535d2d6ca3f2822c5d09a587f5a36cd487a3fb9e408`. Training positives are synthetic transformations |
| Checkpoint SHA-256 and size, as recorded | `identities.checkpointManifestSha256.E3` `e2fb0b34bb287010ddcd0462aa6c03a6b6b30698bf1578e6f965579615ffe320` (a manifest hash, not a weight-file hash). Weight-file size: UNKNOWN. Per-file hashes in `trustedModelFileHashes` are not labelled as E3 weights in the package and are not used here | `identities.checkpointHashes`: five recorded SHA-256 values, in package order (list, not labelled by epoch in the package): `08595d44f63d3002a32e3524b41c36bb95ffaa16995ca16cd73227e277cbf093`, `be88ee233b70ec4f61f794936dee6dcc94142ac9eeed801d69d784cd3ebe5a72`, `74f2eeab1e3b2e7bb4fc3057f6304d6680888f80a3601f6508747c1de713db71`, `0bd8e921d9f0e9adc1d696e11292faa82e87be59ec267d9b8f67f4aa348a2bb0`, `4deacd78f6e2248e847bb377e7dae6767b1e425dd35a3d00a51abb8604f65f50`. Mapping to epoch 2: UNKNOWN. Sizes: UNKNOWN |
| Evidence-package identity | Retrieval package SHA-256 `44ce724068485e9e5304304d8b3ee276c3248aa6e09e879cbd2b47eb0574cc9b` (MATCH), run `retrieval-evidence-20261030-03`. Supersedes `retrieval-evidence-20261030-01` and `-02` | Verifier package SHA-256 `676ad4da5a40ea7a6f040b0c51cc060088689cfd26f23d4a3974b965f1063973` (MATCH), run `pairs-evidence-20261031-03`. Chain: prepare `pairs-prepare-20261030-05`, train `pairs-train-20261030-03`, evaluate `pairs-evaluate-20261031-04` |
| Reload-check record | `measurements.training.reloadPassed` true. Every epoch reload check passed (`reloadChecksPassedEveryEpoch` true). Non-finite step losses: 0 | `reloadEvidenceSha256` `707995933bd7e0f02d8119ae25e5bd5f9fe22f47d47eaeebcef48ef93947f1c2`; `reloadMarkerSha256` `7323f2065c5b32c44fbb4804546999b8e86444d1744357eaa718b94d80541674`; reload evidence recorded as SUCCEEDED |
| Approval and use status | `approvedForUse` false; no product selection | `checkpointAuthorization`: `approvedForUse` false, `selectedForUse` false, `productAuthorizationChanged` false |
| Custodian (project owner) | Custodian: project owner. Availability not confirmed. | Custodian: project owner. Availability not confirmed. |
| Recovery and access procedure | Checkpoints are local-only; redistribution is denied. No checkpoint locator is recorded in the package (`runtimeReview.path` names a review directory, not a checkpoint). Reproduction requires owner confirmation of availability first | Same as model 1: local-only, redistribution denied, no locator recorded. Reproduction requires owner confirmation of availability first |
| Access availability | NOT VERIFIED in this candidate. The owner must confirm | NOT VERIFIED in this candidate. The owner must confirm |
| Redistribution | Local-only; a separate rights decision is required | Redistribution denied; local-only |

Recovery and access, for both models: the checkpoints are local-only and redistribution is denied. The only identity
anchors are the evidence-package identities in section 7.1 (package SHA-256 values) and the manifest and config hashes
in this table. A checkpoint path is not an identity anchor. Reproduction of either model file requires owner
confirmation of availability first. Reproducible access is not established for either model until the custodian
confirms the locator and access prerequisite. A guessed local path is not a locator.

### 7.3 Baseline comparison

Label for the whole table, as specified for this candidate: **validation-only, selection-contaminated,
transformed-copy task, no no-match cases; not held-out; not a quality claim.** Held-out columns are NOT MEASURED.
A NOT MEASURED cell is never a zero.

Values below are from `metrics.*` in the verifier package (macro values, 101 non-exact-copy queries for the
headline rows) and from the controls in the same package. Each value was checked against the package. The
product baseline row is not in these two packages.

| Pipeline | Evaluation partition and purpose | Identity | Precision | Recall | F1 | False findings per query | Held-out P / R / F1 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Product baseline: token union with frozen encoder E0, positive-token all-top-ties emission | Validation only | Not in the two packages; NOT VERIFIED in this candidate | NOT RECORDED | NOT RECORDED | NOT RECORDED | NOT RECORDED | NOT MEASURED |
| Token rank cut (top-1 per query, `tokenRankTopOneCut`; same values for E0 and E3) | Validation only, 101 non-exact-copy queries | Verifier package controls | 0.9900 | 0.7857 | 0.8430 | 0.010 | NOT MEASURED |
| Frozen semantic threshold control, E0 candidates (`semanticScoreThreshold`) | Validation only, 101 queries; threshold selected on validation | Verifier package controls | 0.5192 | 0.7007 | 0.5085 | 4.280 | NOT MEASURED |
| Fine-tuned retrieval E3, semantic threshold control on E3 candidates | Validation only, 101 queries; threshold selected on validation | Verifier package controls | 0.5917 | 0.8432 | 0.5909 | 5.520 | NOT MEASURED |
| Verifier E0 headline (verifier over E0 candidates) | Validation only, 101 queries; verifier threshold 0.1015 selected on validation | Verifier package `pipelines.E0` | 0.1152 | 0.8662 | 0.1530 | 20.435 | NOT MEASURED |
| Verifier E3 headline (verifier over E3 candidates) | Validation only, 101 queries | Verifier package `pipelines.E3` | 0.1129 | 0.8662 | 0.1520 | 20.365 | NOT MEASURED |

Notes on the table:

- The token rank cut row uses a per-query top-1 rank cut, not a global threshold. Its F1 of 0.843 is not the F1 of
  the shipped material rule, which is the positive-token all-top-ties rule. Do not relabel it.
- The verifier outcome recorded in the package is FLAT_OR_NEGATIVE: the headline E0 verifier macro F1 (0.1530)
  is below the best score-threshold control by 0.69 (package `statuses.qualityOutcome.delta`, -0.6900).
- Retrieval, E0 against E3, from the retrieval package. The package records paired differences over 100 seeds
  and 201 queries, not absolute MRR or Recall values. MRR at 20: mean difference 0.0 (100 equal, 0 improved,
  0 worse). Recall at 20: mean difference +0.00333 (2 improved, 0 worse on the E3 row). Outcome:
  NO_MEASURABLE_DIFFERENCE_NEAR_CEILING. Rank-one share 198 of 201 (0.9851).
- Absolute values, read in this change from `artifacts/code_retrieval/runs/retrieval-eval-20261030-02/evaluation-evidence.json`
  (key paths `tables.E0.overall.semantic@20.mrr` and `.recall`, and the same paths under `tables.E3`):
  - E0: MRR@20 0.9925, Recall@20 0.9880.
  - E3: MRR@20 0.9925, Recall@20 0.99133.
  - E3 minus E0 Recall@20 is 0.00333, which matches the package difference.
- Link to the package: VERIFIED (section 7.1). The retrieval package is run `retrieval-evidence-20261030-03`; its
  `chain.evaluation` names the evaluation run these values come from.
- Selection summary, same file (`selection`): status CHOSEN_BY_RULE, choice E3, `approvedForUse` false,
  `trainingManifestsEdited` false, scope "trained epochs only; the frozen model is not a selection candidate". Rule:
  semantic-only, language-equal, seed-macro MRR@20 on validation; tie broken by Recall@20, then the earlier epoch.
- The verifier headline threshold and the semantic thresholds were selected on the same validation data.
  All numbers are selection-contaminated.

Recorded values not in the table above. Each value is copied from the key path shown; nothing was rescored.

| Item | Recorded value | Key path | Status |
| --- | --- | --- | --- |
| Unknown-excluded pairs, verifier candidate prevalence population (E0 / E3) | 1122 / 1122 | `metrics.candidatePrevalence.E0.unknownExcluded`, `metrics.candidatePrevalence.E3.unknownExcluded` | Recorded |
| Unknown-excluded pairs, verifier E0 pipeline | 1122 | `metrics.pipelines.E0.unknownExcluded` | Recorded |
| Headline unknown-excluded count | Not located at the key paths read in this change | — | NOT RECORDED at those paths |
| Known candidate pairs (E0 / E3) | 5929 / 5892 | `metrics.candidatePrevalence.E0.knownCandidatePairs`, `...E3.knownCandidatePairs` | Recorded |
| Known negative pairs (E0 / E3) | 5616 / 5579 | `...knownNegativePairs` | Recorded |
| Positive pairs (E0 / E3) | 313 / 313 | `...positivePairs` | Recorded |
| Negatives per positive, known pairs only (E0 / E3) | 17.94 / 17.82 | `...negativePerPositiveKnownOnly` | Recorded |
| Negatives including unknown pairs per positive (E0 / E3) | 21.53 / 21.41 | `...negativeAndUnknownPerPositive` | Recorded |
| Per-query latency, p50 / p95 seconds (E0 / E3) | E0 0.4617 / 0.6353; E3 0.4568 / 0.6363 | `metrics.latency.E0.perQuerySeconds`, `metrics.latency.E3.perQuerySeconds` | Recorded; 30 timed queries, 3 warm-up queries excluded. Scope label not present at the keys read; no end-to-end latency claim is made |
| Amortized per candidate pair, p50 / p95 seconds (E0 / E3) | E0 0.01555 / 0.02353; E3 0.01559 / 0.02360 | `metrics.latency.E0.amortizedPerCandidatePairSeconds`, `...E3...` | Recorded; same scope note |
| Peak allocated bytes, verifier (E0 and E3 identical) | 1097040896 | `metrics.latency.E0.resources.peakAllocatedBytes` | Recorded |
| Peak reserved bytes, verifier | 1908408320 | `metrics.latency.E0.resources.peakReservedBytes` | Recorded |
| Peak RAM bytes, verifier | 2985721856 | `metrics.latency.E0.resources.ramPeakBytes` | Recorded |
| Peak allocated bytes by encoder label, retrieval (E0 / E1 / E2 / E3) | 522224640 / 522090496 / 522090496 / 522090496 | `measurements.index.peakAllocatedVRAMByLabel` (retrieval package) | Recorded |
| Reviewer acceptance, verifier | NOT_RECORDED | `statuses.reviewerAcceptance` | Recorded status |
| Reviewer acceptance, retrieval | NOT_RECORDED_IN_EVIDENCE | `statuses.reviewerAcceptance` (retrieval package) | Recorded status |
| Absolute MRR@20 / Recall@20 (E0 / E3) | 0.9925 / 0.9880; 0.9925 / 0.99133 | `tables.E0.overall.semantic@20.mrr` and `.recall`; same under `tables.E3` (evaluation evidence) | Recorded |
| Per-language and TypeScript/TSX counts, verifier | NOT MEASURED | — | Not in the verifier package at the keys read |
| Per-stratum counts, verifier | NOT MEASURED | — | Not in the verifier package at the keys read |
| No-match false-alarm rate | NOT MEASURED | — | No no-match cases in these packages |
| Abstention coverage | NOT MEASURED | — | Not recorded |
| Confidence intervals and paired deltas, verifier | NOT MEASURED | — | Not recorded for the verifier |
| Per-language and per-stratum values, retrieval | Exist in the evaluation evidence (`tables.<model>.languages`, `tables.<model>.strata`); not copied here | — | Not rescored; not part of the headline comparison |
| Claim-gate outcome (model-evaluation harness) | NOT MEASURED | — | No harness run was made on these packages |

### 7.4 Reproduction

Replay mode only. Nothing in this section trains, scores or evaluates a model.

Command shape of the validation stage, as documented in `scripts/model_evaluation/README.md` and implemented in
`scripts/model_evaluation/cli.py`:

```text
artifacts/code_analysis/env/ml/Scripts/python.exe -B -m scripts.model_evaluation validate-evidence --run-id <new-evidence-id> --freeze-run-id <recorded-freeze-id> --prepare-run-id <recorded-prepare-id> --candidates-run-id <recorded-candidates-id> --score-run-id <recorded-score-id>
```

Optional flags in the README: `--require-clean` and `--attempt-result <score-id>=<sha256>`.

No model-evaluation chain (freeze, prepare, candidates, score) exists for this candidate: held-out evaluation was
not performed and no corpus was admitted. The command therefore has no real chain to run against. The
identifiers in the two packages belong to the earlier retrieval and verification chains, not to this stage. Any
future run needs the owner to record the chain ids first.

Read closure of `validate-evidence` (`scripts/model_evaluation`). Restricted-named entries are never stat-ed, opened or read.
At the run root they are skipped by name and counted. In the scoring registry a restricted-named entry (in the registry
root or in a key directory) refuses the whole listing, by name, before any stat or open. The restricted-marker predicate
in `guards.py` decides both. Reads per function:

- `validation.run`:
  - reads five JSON documents: the freeze manifest, the bound manifest, the prepared corpus, the candidate artifact
    and the result artifact. Each is checked for canonical bytes and hash links;
  - lists the file names of the four chain run directories (freeze, prepare, candidates, score) and requires each to
    hold only its expected file set (`_directory_files`);
  - reads the registry attempt files of the models through `guards.attempt_files` (`matching_attempts`, and
    `_attempt_records` through it);
  - lists run-root names through `guards.ordinary_names`, then calls `is_file` on `<run root>/<name>/score-result.json`
    for ordinary names only. Restricted-named entries are dropped by name and counted in the output and package key
    `skippedRestrictedNamedRunEntries`; the package records a limitation note with the count, not the names;
  - calls `provenance.capture`: `git rev-parse` and `git status`, and reads the bytes of loaded modules under `scripts/`
    and `services/code-analysis/app`.
- `guards.registered_run_ids` and `guards.matching_attempts`: list the scoring registry through
  `guards.refused_listing`, which refuses the listing when the registry root or any key directory holds a restricted-named
  entry. Only the `attempt-*.json` files of the remaining directories are opened.
- `attempt_listing`: reads `score-result.json` bytes only for registered run ids that are not restricted-named. A
  registered attempt whose run id carries a restricted marker is refused. `score` refuses such run ids when they are
  given to it, so such an attempt can only come from outside the pipeline.
- Directory listings (`os.listdir`) return every name, including restricted-named ones. Those names are discarded by
  name in the same step, before any `stat`, `is_file`, `open` or read.

Tests of the closure (`scripts/model_evaluation/tests/test_read_closure.py`, path-access recorder over `Path` methods):
three functions are covered directly: `validation.unregistered_results` (run-root skip and count),
`guards.registered_run_ids` (registry refusal) and `guards.ordinary_names`. Further tests cover `guards.matching_attempts`
(registry refusal, and a clean registry for ordinary matches), the restricted branch of
`validation.attempt_listing`, and an end-to-end `validation.run` over a complete synthetic chain that contains one
restricted-named run-root entry. Both the path recorder and the test fixture are synthetic.

Doubts recorded earlier, resolved by reading in this change:

- `evidence.validate` (`scripts/pair_verification/evidence.py`): pure function over in-memory documents. No file I/O.
- `score._compute` and `check_pooling`: operate on in-memory dictionaries. No file I/O.
- `candidates.check_identities`: in-memory check. No file I/O.

No doubt remains for these three functions.

Conclusion: the validation read closure is narrowed and bounded to the run root, the scoring registry, the four
chain run directories and the source modules listed above. Restricted-named run and registry entries are never
stat-ed or opened. It is not certified beyond this list: the `git` executable and the operating system read files
outside this code. The owner must review this before any reproduction run is authorized.

Harness tests in this candidate: `scripts/model_evaluation` unittest suite, 280 tests, exit 0 (last run for this change). Demonstration runner
and fixture tests (`scripts/demonstration/tests`): 63 tests, exit 0.

Governed procedure (documented, NOT EXECUTED): freeze, prepare, candidates, score, validate-evidence, with
immutable run ids, bound manifests, an allowed corpus, recorded model identities, environment and rerun policy.
Status: evaluation was not performed and the corpus was not admitted. Nothing in this demonstration runs these
stages.

Environment references (paths only, not provisioned or inspected here): CPU and evidence stages
`artifacts/code_analysis/env/ml`; encoder loading `artifacts/feasibility_pilot/env/ml`; service
`artifacts/code_analysis/env/service`. No installation. GPU use requires separate authorization; on a GPU error,
stop and preserve evidence.

## 8. Feature-freeze list

The following are frozen for the presentation candidate. Changes after the freeze invalidate the affected
evidence and require a focused rerun and review before refreezing.

1. Product commit and dirty-file identity, service and configuration-key inventory.
2. Policy key and digest, verifier mode (disabled), frozen baseline retrieval model and index identity, capability labels.
3. Archive, safety and coverage requirements; live role and ownership checks; audit before release; current tuple
   binding; public snapshot semantics.
4. Fixture bytes, provenance and notices; actor and role mapping; scenario order; isolated runtime namespace;
   launch and stop commands.
5. Research model, tokenizer, preprocessing, corpus, threshold and metric definitions, accepted result identities
   and limitations. No post-result tuning.
6. Narrative, safe captures, comparison table, evidence index and recovery instructions.

Optional work that is outside the freeze and can be done later: commercial deployment and payments, extra browser
coverage, a visible reconnect indicator, larger-scale evaluation, expanded language quality claims, richer visual
polish, and new similarity models.

Mandatory safety, privacy and access behaviour, and a missing trained model, are not optional work.

## 9. Known limitations

### 9.1 Research

- Held-out evaluation was not performed and the corpus was not admitted. Every metric in section 7 is
  validation-only and selection-contaminated. The training positives are synthetic transformations. The
  validation retrieval set is near its ceiling, and EXACT_COPY queries are saturated by construction. Neither
  checkpoint nor a successful reload proves useful quality.
- The verifier outcome is FLAT_OR_NEGATIVE. The retrieval outcome is NO_MEASURABLE_DIFFERENCE_NEAR_CEILING.
- The checkpoints are not approved for product use. Access is NOT VERIFIED. Custodian: project owner. Availability not confirmed.
- Independence of the evaluation set from the training chain is not established. The packages record that
  withheld material was not scored; they do not prove independence.
- No content-level comparison against restricted data is claimed. Labels are agent-produced and are not
  human-verified.
- Evaluation: not performed. The coordinator selected the evaluation option under the owner's delegated fallback rule,
  not a separate owner sign-off. No labels, admitted corpus, parity, latency, checkpoint load or human spot-check.
  No TSX quality claim from parser support or pooled TypeScript counts. Harness results are synthetic only. The
  deployed parity adapter is still open.

### 9.2 Product and verification stage

The verifier and its policy remain disabled. Product uses the frozen baseline retrieval and manual review. No
model-generated percentage, legal verdict or originality guarantee is given.

The limitations below are carried from the verification stage. They are stated per area and are not ranked.

1. Browser journeys drive the production frontend against the schema-validated stub. Backend publication evidence comes only from the HTTP and PostgreSQL publication tests.
2. Late older-version REJECT and REQUEST_CHANGES are store-level only.
3. Role and access: positive submit, appeal, coverage inventory and retry are unproven. A coverage-controller role-gate mutation survived.
4. Concurrency: no submit-versus-owner-edit race; case audit and notification effects unasserted; history asserted on one race only; "two reviewers" means decision versus claim and same-Moderator distinct operations.
5. Tuple binding: six production-workflow tests plus four guard or input tests; the tuple-by-tuple mutation map is incomplete.
6. Late completion and replay: a replay-gate mutation survived; outbox dispatch and consumer replay are unproven.
7. Safety: only one `../` ZIP and one test-string ZIP reach HTTP. No bomb, special tar, empty or oversized archive row. The reason check is only a leading letter, not specific text and next action. Object storage in that host is a test replacement. Analysis disabled. No scanner-branch mutation. No Moderator action. "Blocked" means seller download 404 and no publication; no quarantine route or state was asserted.
8. Privacy: actual queue HTTP cursor pages were walked. Only `PRIVATE_SOURCE` has a queue-page kill. The private rationale is checked only after the decision (public detail, seller review, outbox, audit, history), not on queue pages. The other-seller note and source are checked on the owner's response read, not on a live queue item. Notification and seller-row scans may be empty at runtime. The cross-seller check is one 404. Buyer body, BFF, cache, dispatched notification and log sinks are unreached. No whole-flow privacy claim.
9. Moderation events: LongPolling stub only; no real SignalR server, WebSocket or reconnect; no event-handler mutation.
10. Browser: no reload, retry or storage sweep, no full keyboard, contrast or screen-reader evidence, no per-journey mutation evidence.
11. Copy: the verdict denylist covers frontend source and backend error messages (the terms of the backend test). Notification text and public or buyer score absence have no mechanical test. Editorial quality is human source review.
12. Evaluation: not performed. No labels, admitted corpus, parity, latency, checkpoint load or human spot-check. No TSX quality claim from parser support or pooled TypeScript counts. Harness results are synthetic only. The deployed parity adapter is still open.
13. Environment: Chromium and Windows only. Two live tests skip by default. One service pytest skip has an unknown reason. Native exit codes are captured for `demo-full-2`, `demo-full-4` and `demo-d1-2` (all 0). The native exit codes of `demo-full-1` and `demo-full-3` are UNKNOWN (not captured).
14. Residuals with no row in section 5: reject-to-appeal is not one continuous journey; same-version draft metadata and public snapshot behaviour are not wholly covered.
15. No scenario was run live in this candidate. The recorded runs run test lanes only, and their aggregate status is LANES_PASS, which is not scenario acceptance.
16. Publication matrix: revoked role, deleted and safety states, bundle, library, bundle checkout, pending price and tags not covered.
17. User-visible unsupported, partial and TSX reasons and stale assessment are not proven end to end.
18. No temporary-file or plaintext residue matrix across success, failure, cancel, lease and restart.
19. No cache coherence matrix. No deletion-state matrix.
20. Price-only separation: by design in source (price sits outside the material revision and the decision tuple has no price). No traced example and no price-only test in these lanes; pending price is not covered.

Additional limitations from this candidate:

- Account provisioning for a live manual run is not scripted. The HTTP and store lanes create their own users inside
  their containers.
- The live pending-version acceptance lane and the live upload acceptance are opt-in and skipped by default. A skip is
  not a pass.
- Cross-browser, screen-reader, contrast, large-scale, production payment, Linux, multi-instance and deployment
  behaviour are not covered.
- Browser evidence uses a local stub. It does not prove authorization, concurrency or real worker behaviour.
  Screenshots do not substitute for HTTP, PostgreSQL or live evidence.
- Evaluation-harness parity and resource limits are as stated in the harness README. No measured end-to-end latency
  claim is made from the recorded timings.

## 10. Cleanup

- Stop only the processes you started, by the process IDs recorded at launch, using their supported graceful
  shutdown. Use a forced stop only for those IDs. Never kill by name.
- The scenario runner stops only its own child process tree when a lane times out. It does not stop other
  processes.
- Stop the Compose services you started with `docker compose stop <services>`. Do not run `down -v`. Do not
  remove volumes.
- Verify that the ports used (5088, 3000, 8081, 5432, 8333, 6379, 3310, 1025, 8025, 3100, 3999) are released.
- Keep encrypted and purchased blobs and audit evidence according to the existing retention rules. Do not delete
  directories recursively without a resolved containment check and a specific authorization.
- Scratch output stays under `artifacts/demonstration/`, which is git-ignored.
