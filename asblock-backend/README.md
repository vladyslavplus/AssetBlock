# asblock-backend

The backend API and service layer for AssetBlock (non-commercial personal project), built with .NET 10 and PostgreSQL.

## Local configuration (secrets)

Tracked `appsettings.json` keeps **placeholders only** (no real secrets). Local overrides belong in:

1. **Ignored** `appsettings.Development.json` (already in `.gitignore`), and/or
2. **.NET User Secrets** (recommended when you want secrets outside any JSON file)

The API validates required options at startup (`ValidateOnStart`) and fails fast when mandatory configuration is missing or invalid.

### 1. User Secrets (optional alternative to Development JSON)

From `asblock-backend/`:

```bash
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "<your-postgres-connection-string>" --project AssetBlock.WebApi
dotnet user-secrets set "ConnectionStrings:Redis" "<your-redis-connection-string-or-empty>" --project AssetBlock.WebApi
dotnet user-secrets set "Jwt:Key" "<hmac-signing-key-at-least-32-characters>" --project AssetBlock.WebApi
dotnet user-secrets set "Storage:Provider" "SeaweedFs" --project AssetBlock.WebApi
dotnet user-secrets set "SeaweedFs:Endpoint" "http://127.0.0.1:8333" --project AssetBlock.WebApi
dotnet user-secrets set "SeaweedFs:Bucket" "assets" --project AssetBlock.WebApi
dotnet user-secrets set "SeaweedFs:AccessKey" "assetblock" --project AssetBlock.WebApi
dotnet user-secrets set "SeaweedFs:SecretKey" "dev_seaweedfs_secret" --project AssetBlock.WebApi
dotnet user-secrets set "SeaweedFs:UseSsl" "false" --project AssetBlock.WebApi
dotnet user-secrets set "Encryption:CurrentKeyId" "k1" --project AssetBlock.WebApi
dotnet user-secrets set "Encryption:Keys:k1" "<base64-encoded-32-byte-aes-key>" --project AssetBlock.WebApi
```

MinIO compatibility provider (after `docker compose --profile minio up -d`):

```bash
dotnet user-secrets set "Storage:Provider" "Minio" --project AssetBlock.WebApi
dotnet user-secrets set "Minio:Endpoint" "http://127.0.0.1:9000" --project AssetBlock.WebApi
dotnet user-secrets set "Minio:Bucket" "assets" --project AssetBlock.WebApi
dotnet user-secrets set "Minio:AccessKey" "assetblock" --project AssetBlock.WebApi
dotnet user-secrets set "Minio:SecretKey" "dev_minio_secret" --project AssetBlock.WebApi
dotnet user-secrets set "Minio:UseSsl" "false" --project AssetBlock.WebApi
```

> Switching `Storage:Provider` does **not** migrate existing encrypted objects. Keep using the provider that holds your blobs, or re-upload.

Optional Stripe (omit all Stripe keys to run with payments inactive):

```bash
dotnet user-secrets set "Stripe:SecretKey" "<stripe-secret-key>" --project AssetBlock.WebApi
dotnet user-secrets set "Stripe:WebhookSecret" "<stripe-webhook-secret>" --project AssetBlock.WebApi
dotnet user-secrets set "Stripe:SuccessUrl" "http://localhost:3000/checkout/success" --project AssetBlock.WebApi
dotnet user-secrets set "Stripe:CancelUrl" "http://localhost:3000/checkout/cancel" --project AssetBlock.WebApi
```

For local webhook forwarding, start the API on its checked-in HTTP profile (`http://localhost:5088`), then run:

```bash
stripe listen --forward-to http://localhost:5088/api/payments/webhook
```

Set `Stripe:WebhookSecret` to the `whsec_...` printed by that active listener and restart the API. The Stripe CLI must be logged into the same test account as `Stripe:SecretKey`. A listener only forwards events it receives while running; use Stripe CLI event resend for an already completed local checkout. Never paste or commit either secret.

### Collections vs Bundles

- **Collection** is editorial only: one seller curates up to 50 of their own assets, with DRAFT / PUBLISHED / ARCHIVED lifecycle. Collections have no price, checkout, entitlement, or license. Public reads return only PUBLISHED collections that still have at least one active (non-soft-deleted) item.
- **Bundle** is a paid same-seller product: 2–20 distinct active assets, one fixed USD price lower than the summed list-price snapshot, append-only immutable revisions. Multi-seller bundles and revenue splitting are out of scope. In v1 a buyer who already owns any bundle item cannot purchase that bundle (no partial pricing).

### Generalized checkout and orders

Direct asset and bundle payments share one commerce model:

1. `POST /api/payments/checkout` with `{ assetId }` or `POST /api/payments/checkout/bundles` with `{ bundleId }`.
2. Server creates a durable `CheckoutIntent` + immutable `CheckoutIntentItem` rows + `CheckoutReservation` rows `(UserId, AssetId)` in a short DB transaction (no Stripe/network calls inside).
3. Stripe Checkout Session is created after commit; metadata contains only `checkoutIntentId` and `userId`; idempotency key = checkout intent id.
4. Browser success redirect never creates entitlements. Stripe webhook `checkout.session.completed` is the only payment-completion authority: it verifies signature/amount/currency/session, then atomically creates `Order` + `OrderLine`s + per-asset `Purchase` entitlements, one buyer receipt/order-ready notification, and one seller sale email/notification.
5. Historical paid price/currency live on `Order` / `OrderLine`. `Purchase` remains the per-asset entitlement used by library, downloads, and reviews.

Local webhook flow remains: API on `http://localhost:5088` + `stripe listen --forward-to http://localhost:5088/api/payments/webhook`.

Generate a local AES-256 key (32 bytes, Base64):

```bash
# PowerShell
[Convert]::ToBase64String((1..32 | ForEach-Object { [byte](Get-Random -Maximum 256) }))

# OpenSSL
openssl rand -base64 32
```

### 2. Configuration keys

| Key | Required | Notes |
|-----|----------|--------|
| `ConnectionStrings:DefaultConnection` | Yes | PostgreSQL |
| `ConnectionStrings:Redis` | No | Empty → in-memory cache |
| `Jwt:Issuer` / `Jwt:Audience` | Yes | Non-secret; placeholders in tracked config |
| `Jwt:Key` | Yes | ≥ 32 characters |
| `Jwt:AccessTokenMinutes` / `Jwt:RefreshTokenDays` | Yes | Positive integers |
| `Storage:Provider` | Yes | `SeaweedFs` (local default) or `Minio` (case-insensitive). Exactly one active provider; no fallback/dual-write. |
| `SeaweedFs:Endpoint` | When SeaweedFs | `host:port` or absolute `http`/`https` URI (no path/query); `UseSsl` must match scheme when URI is used |
| `SeaweedFs:Bucket` | When SeaweedFs | e.g. `assets` |
| `SeaweedFs:AccessKey` / `SeaweedFs:SecretKey` | When SeaweedFs | Match compose `AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` for local mini |
| `SeaweedFs:UseSsl` | When SeaweedFs | `false` for local HTTP SeaweedFS |
| `Minio:Endpoint` | When Minio | `host:port` or absolute `http`/`https` URI (no path/query); `UseSsl` must match scheme when URI is used |
| `Minio:Bucket` | When Minio | e.g. `assets` |
| `Minio:AccessKey` / `Minio:SecretKey` | When Minio | No code fallbacks |
| `Minio:UseSsl` | When Minio | `false` for local HTTP MinIO |
| `Encryption:CurrentKeyId` / `Encryption:Keys:*` | Yes | Non-empty current key ID present in keyring; each key Base64 of exactly 32 bytes |
| `Stripe:*` | No | If **any** Stripe field is set, all of `SecretKey`, `WebhookSecret`, `SuccessUrl`, `CancelUrl` are required |
| `Email:Provider` | Yes | Must be `Smtp` (case-insensitive) |
| `Email:FromName` / `Email:FromAddress` | Yes | From mailbox for transactional mail |
| `Email:PublicAppBaseUrl` | Yes | Absolute `http`/`https` SPA origin for fixed template links |
| `Email:MessageIdDomain` | Yes | Domain used in deterministic RFC Message-Id values |
| `Email:Smtp:Host` / `Port` / `Security` / `TimeoutSeconds` | Yes | Local Mailpit: `localhost` / `1025` / `NONE` / `30`; credentials both empty or both set |
| `DataProtection:KeysPath` | Yes | Dedicated key-ring directory (leaf name `dataprotection-keys` or `assetblock-dataprotection-keys*`); must survive API restarts; never commit keys (gitignored). A marker file `.assetblock-dataprotection-keys` is created; refuse arbitrary existing folders. |
| `DataProtection:ProtectionMode` | Cond. | `Dpapi` (Windows), `Certificate`, `Kms`, or `None` (non-Production only). Empty → Dpapi on Windows, None in Development/IntegrationTesting; **Production on non-Windows requires Certificate or Kms** (fail-fast; plaintext not allowed). |
| `DataProtection:CertificatePath` / `CertificatePassword` / `CertificateThumbprint` | Cond. | Certificate mode: PFX path+password and/or store thumbprint from secret store only. |
| `DataProtection:KmsKeyId` | Cond. | Required when `ProtectionMode=Kms` (deployment must wire vault/KMS protector; mode currently fails until wired). |

### 3. Docker / Next.js `.env`

- Backend Docker Compose uses `asblock-backend/.env` (gitignored). Copy from `.env.example` — safe template only.
- Next.js uses its own ignored `.env` / `.env.local` from `asblock-frontend/.env.example`.
- Do not put API Stripe/AES/JWT secrets into tracked files.

### 4. Stripe key rotation (manual)

If Stripe secret or webhook keys were ever committed or shared, **rotate/revoke them in the Stripe Dashboard** yourself. This repository cannot revoke remote keys.

### 5. AES key rotation (keyring)

To rotate AES encryption keys, add the new 32-byte Base64 key under `Encryption:Keys:<newId>` and set `Encryption:CurrentKeyId` to `<newId>`. Retain previous keys in `Encryption:Keys` so previously encrypted objects can continue to be decrypted. If an old key is removed, any objects encrypted with it can no longer be decrypted. Clearing local storage buckets and/or the dev database is a **manual** step after you confirm there is nothing valuable to keep (take a backup first if unsure). Agents must not wipe Docker volumes or databases for you. Switching `Storage:Provider` also does not migrate blobs between SeaweedFS and MinIO.

## Typical commands

```bash
dotnet restore asblock-backend.slnx
dotnet build asblock-backend.slnx
dotnet test asblock-backend.slnx
dotnet run --project AssetBlock.WebApi
```

### Seller analytics

- **Gross revenue** only (integer USD cents); commerce from completed `Order`/`OrderLine` rows after Stripe webhook fulfillment.
- **UTC ranges:** `from` inclusive, `to` exclusive, 1–366 days; `engagementAvailableFrom` marks first retained telemetry.
- **Engagement:** append-only `analytics_events`, 400-day retention, daily rollups recomputed by `AnalyticsAggregationWorker` (advisory lock).
- **Rate limits:** `ANALYTICS_EVENTS` (120/min/partition) and `SELLER_ANALYTICS_SALES_EXPORT` (10/hour/seller) use Redis-protocol fixed windows in Staging/Production (`ConnectionStrings:Redis` required; Valkey locally). Development/IntegrationTesting fall back to in-memory limiters when Redis connection is unset. During Redis outages the API logs once, returns 202 for telemetry and 503 for CSV export for a short backoff window without per-request Redis calls.
- **BFF signing:** `AnalyticsRateLimiting:BffSigningSecret` (min 32 chars) + frontend `ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET`.
- **Routes:** `GET /api/seller/analytics/*`, `POST /api/analytics/events`, CSV at `GET /api/seller/analytics/sales/export`.
- **Worker:** `AnalyticsAggregationWorker` recomputes UTC daily rollups every five minutes (advisory lock) and deletes raw events older than 400 days.
- **Stripe local webhook:** `stripe listen --forward-to http://localhost:5088/api/payments/webhook`

### Tests

The solution contains five test projects: three focused unit-test projects and two PostgreSQL/Testcontainers integration-test projects.

| Project | Purpose | Needs Docker |
|---------|---------|--------------|
| `AssetBlock.*.Tests` (unit) | Isolated logic: validators, crypto, cache, password hashing, handler mocks | No |
| `AssetBlock.Infrastructure.IntegrationTests` | EF Core stores, mappings, and migrations against real PostgreSQL via Testcontainers | Yes |
| `AssetBlock.WebApi.IntegrationTests` | HTTP pipeline, controllers, auth, routing, model binding | Yes |

Infrastructure and Web API integration tests use Testcontainers; a running **Docker daemon** is required. Do not start PostgreSQL manually for these projects.

```bash
dotnet test AssetBlock.Infrastructure.IntegrationTests/AssetBlock.Infrastructure.IntegrationTests.csproj
dotnet test AssetBlock.WebApi.IntegrationTests/AssetBlock.WebApi.IntegrationTests.csproj
```

Ryuk (Testcontainers resource reaper) stays **enabled by default** so killed test processes still clean containers. If Docker Desktop wedges on Ryuk (`Created` but never `Started`), opt out for that shell only:

```powershell
$env:ASSETBLOCK_DISABLE_RYUK = "true"
dotnet test AssetBlock.Infrastructure.IntegrationTests/AssetBlock.Infrastructure.IntegrationTests.csproj
```

Do not set this in CI unless Ryuk is proven broken there. Fixtures also apply a 2-minute start timeout so a wedged daemon fails fast instead of hanging forever.

Bring up local app dependencies with `docker-compose.yml` in this folder when running the API outside tests.

### Aspire Dashboard (Local Observability)

The `docker-compose.yml` includes the official standalone **Aspire Dashboard** container for OpenTelemetry traces, metrics, and logs. It does not run an Aspire AppHost; it functions purely as an OTLP receiver and UI viewer.

```bash
docker-compose up -d aspire-dashboard
```

- **Dashboard UI**: `http://localhost:18888` (unsecured/anonymous mode for local dev)
- **OTLP gRPC receiver**: `http://localhost:4317`
- Ports bind to `127.0.0.1` only.

When you run the API directly on the host (e.g. from an IDE or `dotnet run`), it exports telemetry to `http://127.0.0.1:4317`. If the API is later containerized within Compose, it should use the internal service DNS (`http://aspire-dashboard:18889`).

Observability is disabled by default in tracking configuration so a missing dashboard does not break startup. Enable it via user secrets or environment variables:

```bash
dotnet user-secrets set "Observability:Enabled" "true" --project AssetBlock.WebApi
```

If the exporter cannot reach the dashboard, telemetry is dropped. The API endpoints and health checks will remain healthy and operational.

### Mailpit (local SMTP inbox)

Mailpit is a **development SMTP catcher**, not an `IEmailSender` implementation and never a production email endpoint. The API sends through `SmtpEmailSender` (MailKit); point SMTP at Mailpit locally.

```bash
docker-compose up -d mailpit
```

- SMTP (host-run API): `localhost:1025`, `Security=NONE`, empty username/password
- Inbox UI: `http://localhost:8025`
- UI/SMTP ports bind to `127.0.0.1` only
- Inbox resets with the container (no persistent volume in v1)

Example User Secrets for local Mailpit and Data Protection:

```bash
dotnet user-secrets set "Email:Provider" "Smtp" --project AssetBlock.WebApi
dotnet user-secrets set "Email:FromName" "AssetBlock" --project AssetBlock.WebApi
dotnet user-secrets set "Email:FromAddress" "noreply@localhost" --project AssetBlock.WebApi
dotnet user-secrets set "Email:PublicAppBaseUrl" "http://localhost:3000" --project AssetBlock.WebApi
dotnet user-secrets set "Email:MessageIdDomain" "mail.localhost" --project AssetBlock.WebApi
dotnet user-secrets set "Email:Smtp:Host" "localhost" --project AssetBlock.WebApi
dotnet user-secrets set "Email:Smtp:Port" "1025" --project AssetBlock.WebApi
dotnet user-secrets set "Email:Smtp:Security" "NONE" --project AssetBlock.WebApi
dotnet user-secrets set "Email:Smtp:TimeoutSeconds" "30" --project AssetBlock.WebApi
dotnet user-secrets set "DataProtection:KeysPath" "dataprotection-keys" --project AssetBlock.WebApi
dotnet user-secrets set "DataProtection:ProtectionMode" "Dpapi" --project AssetBlock.WebApi
```

Alternatively, set in `appsettings.Development.json` (already gitignored):

```json
{
  "DataProtection": {
    "KeysPath": "dataprotection-keys",
    "ProtectionMode": "Dpapi"
  }
}
```

The `dataprotection-keys/` directory is gitignored (`**/dataprotection-keys/`). Never commit key ring files. Restrict filesystem ACL on that directory outside the process (deployment-owned); the API does not rewrite NTFS ACLs on arbitrary paths.

**Linux / container Production:** set `ProtectionMode` to `Certificate` (PFX + password from secret store) or `Kms` after wiring a vault protector. Empty/`None` **fail-fast** in Production on non-Windows — plaintext key rings are not allowed.
### Email lifecycle

- Provider-neutral `IEmailSender` + SMTP transport; Mailpit catches all mail locally.
- **Registration anti-enumeration:** `POST /api/auth/register` returns 202 for both new and existing email addresses and does not issue an authenticated session. New accounts receive verification mail; existing accounts receive a generic registration-attempt notice. Callers must sign in separately after registration.
- **Verification on register:** every new account receives an `EMAIL_VERIFICATION` action link via outbox (`EMAIL_ACTION_DISPATCH`). The link is time-limited (24 h) and generated at delivery time by `EmailActionLinkProtector` (ASP.NET Core Data Protection). No token or URL is stored in the outbox payload.
- **Verified-email authorization:** named policy `VERIFIED_EMAIL` (`AuthorizationPolicies.VERIFIED_EMAIL`) loads current `EmailVerifiedAt` from the database (not a JWT claim). Failure returns HTTP 403 `application/problem+json` with `ERR_EMAIL_NOT_VERIFIED`.
  - **Blocked until verified:** asset upload/update/delete/tag writes; publish version; checkout; review create; profile and socials writes; all Admin mutations and audit-log read (Admin role **and** verified email — no Admin bypass).
  - **Still allowed while unverified:** public catalog reads; register/login/refresh; password-reset request/confirm; email verification confirm/resend; email-change request/confirm; `GET /api/users/me`; password change; notifications; own purchases/listings reads; download of already owned or author assets (latest entitled or specific version); public/active asset version history; SignalR hub; payment capabilities; Stripe webhook fulfillment.
- **Password reset confirms mailbox:** successful `ConfirmPasswordReset` sets `EmailVerifiedAt` when it was null (same transaction as the password change). Audit metadata may include `emailVerifiedByPasswordReset=true`. Login, refresh, current-password checks, and ordinary password change do **not** mark email verified.
- **Resend verification:** authenticated users can request a new link; enforces 60 s cooldown and returns `ERR_EMAIL_ACTION_COOLDOWN` if too soon.
- **Password reset (no enumeration):** `POST /api/auth/password-reset/request` always returns 202 regardless of whether the email is registered; cooldown is silently respected. Reset link is valid for 30 min.
- **Email change:** requires current password + desired new address. Issues an `EMAIL_CHANGE` action to the new mailbox (before `User.Email` is updated). Confirm endpoint swaps the address and revokes sessions; the new mailbox is treated as verified.
- **Transactional notices:** password change and email change send `EMAIL_DISPATCH` notices to the old address via `ITransactionalEmailComposer`.
- **Outbox types:** user-facing action emails use `EMAIL_ACTION_DISPATCH`; notice emails (no link, no token) use `EMAIL_DISPATCH`.
- **Security:** no token, URL, password, or email body in any outbox payload row. Action links use `#token=` fragments (not query strings) so browsers do not send the secret to servers/proxies/logs on navigation. `EmailActionLinkProtector` produces time-limited, tamper-evident tokens. Outbox `EMAIL_ACTION_DISPATCH` carries `ActionVersion` so stale retries after resend are skipped. Logs include outbox id, template kind, and recipient user id only.
- **Delivery:** at-least-once; idempotent stale-action check before send; no extra SMTP retry layer beyond outbox lease/backoff.
- **Mailpit:** development SMTP catcher only, not a production endpoint. Run with `docker-compose up -d mailpit`; inbox at `http://localhost:8025`.

### Audit log

Append-only `audit_logs` records security-sensitive and business-critical mutations (auth, account, assets, admin catalog writes, reviews, completed purchases). It is **not** a replacement for:

- **Serilog** — technical HTTP/request diagnostics;
- **transactional outbox** — reliable side-effect delivery after commit;
- **seller analytics** — seller dashboard gross revenue, engagement telemetry, CSV export (distinct from audit log and Serilog).

Success DB mutations write the audit row in the same `IUnitOfWork` transaction as business changes. Failure/denied paths use best-effort writes so audit infrastructure outages do not change the original API result. Metadata is allowlisted only (no passwords, tokens, Stripe payloads, comments, or full entity snapshots). `ActorUserId` has no FK to `users`.

**Admin read:** `GET /api/admin/audit-logs` (Admin role). Frontend admin tab proxies through BFF `GET /api/admin/audit-logs`. IP and User-Agent are operational personal data; there is no automatic retention cleanup yet.

**Extension rule:** when adding a critical mutation, decide whether it needs an audit event, pick stable `AuditActions` / `AuditResourceTypes` values, and list allowlisted metadata fields explicitly in the handler.

### Health checks

- `GET /health/live` reports process liveness only and does not probe external dependencies.
- `GET /health/ready` probes PostgreSQL, the configured storage provider, Redis when configured, and ClamAV when `AssetProcessing:Enabled` is true. ClamAV readiness requires a parseable `VERSION` response whose signature database age is within `ClamAv:MaxSignatureAge` (default 72h, allowed 1h–7d). Liveness is unchanged.

Both endpoints return a small JSON report. Readiness returns HTTP 503 while any required dependency is unavailable.

### Asset processing, archives, and ClamAV

New uploads stay seller-visible and out of the public catalog until archive inspection and a clean ClamAV scan succeed. A pending version never replaces a previous READY current version; rejection or `PROCESSING_FAILED` leaves the READY version in place. Purchasers cannot download pending, rejected, or failed versions; authors may still download their own files.

`ArchiveSafetyInspector` inspects only the outer ZIP/TAR/TAR.GZ container (path safety, entry types, sizes, and compression ratio). `MaxPathDepth` is filesystem path depth, not nested-archive depth. Nested archives are ordinary files to the inspector; ClamAV enforces nesting through `MaxRecursion`, `MaxScanSize`, `MaxFiles`, and `AlertExceedsMax yes` in `clamav/clamd.env`.

ClamAV uses clamd `INSTREAM` over TCP. `ClamAv:DaemonMaxStreamBytes` is an optional hint (`0` disables inference). A non-zero value must match the daemon `StreamMaxLength` exactly. Ambiguous disconnects are retryable `SCANNER_UNAVAILABLE`; explicit or known stream limits are terminal `SCANNER_LIMIT_EXCEEDED`.

Local daemon:

```bash
docker-compose up -d clamav
```

clamd binds to `127.0.0.1:3310`. Signature data lives on the `clamav_data` volume. Enable processing in Development (`AssetProcessing:Enabled` and `ClamAv:Enabled`).

### Listing Copilot (optional AI)

Sellers can request a listing suggestion for a **READY** version that already has archive analysis. The API never writes title/description/category/tags automatically; Apply in the seller UI only fills the edit form.

- `POST /api/users/me/asset-versions/{assetVersionId}/listing-copilot` (verified email, local rate limit) enqueues one idempotent `LISTING_COPILOT` job (`DefinitionVersion` 1). Repeated POST returns the same `jobId` with `202 Accepted`.
- `GET /api/users/me/asset-versions/{assetVersionId}/listing-copilot` returns the stored suggestion or `404`. It never includes prompts, raw provider JSON, tokens, or `providerRequestId`.
- Disabled AI (`Ai:Enabled=false`) returns `ERR_AI_DISABLED` and does not enqueue.

Models come from typed configuration only. Local Development lists ordered OpenRouter models under `Ai:OpenRouter:Models`. Deployment overrides use standard .NET configuration providers (environment variables, user secrets). Configuration changes apply after restart. Inactive provider sections are not validated.

Environment overrides (do not commit real values):

```bash
Ai__Enabled=true
Ai__Provider=OpenRouter
Ai__PromptPolicyVersion=listing-copilot-v1
Ai__OpenRouter__ApiKey=          # user secrets only
Ai__OpenRouter__Models__0=model-a
Ai__OpenRouter__Models__1=model-b
Ai__Ollama__BaseUrl=http://127.0.0.1:11434
Ai__Ollama__Model=               # exact local tag
Ai__Ollama__Digest=              # exact sha256: digest from ollama show /api/tags
```

### Local Ollama (optional AI)

AI generation is disabled in tracked config (`Ai:Enabled=false`). Marketplace and API startup do not call OpenRouter or Ollama until AI is explicitly enabled.

OpenRouter is the default provider. Ollama is an explicit alternative with no automatic fallback. AssetBlock does not start Ollama, ping it at startup, or pull models.

Native setup:

1. Install Ollama on the host and start the local daemon (`http://127.0.0.1:11434`).
2. Pull a model yourself with the Ollama CLI. Use that exact model tag in `Ai:Ollama:Model`.
3. Set `Ai:Ollama:Digest` to the exact `sha256:` digest from `ollama show` or `/api/tags`. Generation calls `/api/tags` first and refuses to run unless name and digest match.
4. Set `Ai:Enabled=true`, `Ai:Provider=Ollama`, and keep `Ai:Ollama:BaseUrl` as a loopback HTTP URL. Put secrets in user secrets or environment variables, never in tracked files.

OpenRouter requires an API key and a non-empty ordered distinct `Ai:OpenRouter:Models` list (1–16 unique ids). That list is both the allowlist and the OpenRouter fallback order. Requests send `require_parameters=true` and `data_collection=deny`. Optional `Ai:OpenRouter:ZeroDataRetention` adds `zdr=true` and may reduce available endpoints. The returned `actualModel` must match a configured id exactly (ordinal); otherwise the call is terminal `ERR_AI_MODEL_NOT_ALLOWED` and no `ModelRevision` is stored. There is no OpenRouter → Ollama fallback. Tracked `appsettings.json` keeps `Ai:Enabled=false` and empty models.

