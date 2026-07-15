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
| `Elasticsearch:Url` / `Elasticsearch:DefaultIndex` | Yes | Absolute URL + index name |
| `Stripe:*` | No | If **any** Stripe field is set, all of `SecretKey`, `WebhookSecret`, `DefaultSuccessUrl`, `DefaultCancelUrl` are required |

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

### Health checks

- `GET /health/live` reports process liveness only and does not probe external dependencies.
- `GET /health/ready` probes PostgreSQL, the configured MinIO bucket, Elasticsearch, and Redis when a Redis connection string is configured.

Both endpoints return a small JSON report. Readiness returns HTTP 503 while any required dependency is unavailable.
