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
dotnet user-secrets set "Minio:AccessKey" "<minio-access-key>" --project AssetBlock.WebApi
dotnet user-secrets set "Minio:SecretKey" "<minio-secret-key>" --project AssetBlock.WebApi
dotnet user-secrets set "Encryption:KeyBase64" "<base64-encoded-32-byte-aes-key>" --project AssetBlock.WebApi
```

Optional Stripe (omit all Stripe keys to run with payments inactive):

```bash
dotnet user-secrets set "Stripe:SecretKey" "<stripe-secret-key>" --project AssetBlock.WebApi
dotnet user-secrets set "Stripe:WebhookSecret" "<stripe-webhook-secret>" --project AssetBlock.WebApi
dotnet user-secrets set "Stripe:DefaultSuccessUrl" "http://localhost:3000/payment/success" --project AssetBlock.WebApi
dotnet user-secrets set "Stripe:DefaultCancelUrl" "http://localhost:3000/payment/cancel" --project AssetBlock.WebApi
```

For local webhook forwarding, start the API on its checked-in HTTP profile (`http://localhost:5088`), then run:

```bash
stripe listen --forward-to http://localhost:5088/api/payments/webhook
```

Set `Stripe:WebhookSecret` to the `whsec_...` printed by that active listener and restart the API. The Stripe CLI must be logged into the same test account as `Stripe:SecretKey`. A listener only forwards events it receives while running; use Stripe CLI event resend for an already completed local checkout. Never paste or commit either secret.

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
| `Minio:Endpoint` | Yes | `host:port` or absolute `http`/`https` URI (no path/query); `UseSsl` must match scheme when URI is used |
| `Minio:Bucket` | Yes | e.g. `assets` |
| `Minio:AccessKey` / `Minio:SecretKey` | Yes | No code fallbacks |
| `Minio:UseSsl` | Yes | `false` for local HTTP MinIO |
| `Encryption:KeyBase64` | Yes | Base64 of exactly 32 bytes |
| `Stripe:*` | No | If **any** Stripe field is set, all of `SecretKey`, `WebhookSecret`, `DefaultSuccessUrl`, `DefaultCancelUrl` are required |
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

### 5. AES key rotation (local data)

After changing `Encryption:KeyBase64`, previously encrypted MinIO objects cannot be decrypted with the new key. Clearing local MinIO buckets and/or the dev database is a **manual** step after you confirm there is nothing valuable to keep (take a backup first if unsure). Agents must not wipe Docker volumes or databases for you.

## Typical commands

```bash
dotnet restore asblock-backend.slnx
dotnet build asblock-backend.slnx
dotnet test asblock-backend.slnx
dotnet run --project AssetBlock.WebApi
```

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

Bring up local app dependencies with `docker-compose.yml` in this folder when running the API outside tests.

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
- **seller analytics** — product metrics (separate model later).

Success DB mutations write the audit row in the same `IUnitOfWork` transaction as business changes. Failure/denied paths use best-effort writes so audit infrastructure outages do not change the original API result. Metadata is allowlisted only (no passwords, tokens, Stripe payloads, comments, or full entity snapshots). `ActorUserId` has no FK to `users`.

**Admin read:** `GET /api/admin/audit-logs` (Admin role). Frontend admin tab proxies through BFF `GET /api/admin/audit-logs`. IP and User-Agent are operational personal data; there is no automatic retention cleanup yet.

**Extension rule:** when adding a critical mutation, decide whether it needs an audit event, pick stable `AuditActions` / `AuditResourceTypes` values, and list allowlisted metadata fields explicitly in the handler.

### Health checks

- `GET /health/live` reports process liveness only and does not probe external dependencies.
- `GET /health/ready` probes PostgreSQL, the configured MinIO bucket, and Redis when a Redis connection string is configured.

Both endpoints return a small JSON report. Readiness returns HTTP 503 while any required dependency is unavailable.
