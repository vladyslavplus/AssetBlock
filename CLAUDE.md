# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Repository structure

Monorepo for **AssetBlock**, a marketplace for digital intellectual property:
- `asblock-backend/` — .NET 10 Web API (Clean Architecture: Domain → Application → Infrastructure → WebApi)
- `asblock-frontend/` — Next.js App Router application

This is a non-commercial/academic project.

## Backend commands (from `asblock-backend/`)

```bash
dotnet restore asblock-backend.slnx
dotnet build asblock-backend.slnx
dotnet test asblock-backend.slnx                    # All tests
dotnet test AssetBlock.Application.Tests            # Unit tests only (no Docker)
dotnet test AssetBlock.Infrastructure.IntegrationTests  # Requires Docker
dotnet run --project AssetBlock.WebApi
dotnet ef database update --project AssetBlock.Infrastructure --startup-project AssetBlock.WebApi
```

**Local dependencies:** `docker-compose.yml` in backend folder (PostgreSQL, Redis, MinIO, Mailpit).

## Frontend commands (from `asblock-frontend/`)

```bash
pnpm install
pnpm dev
pnpm run check    # Lint + typecheck
pnpm run build
```

## Architecture overview

### Backend layer responsibilities

- **Domain:** Entities, DTOs, options, constants, error codes, domain exceptions, and all `I*Store`/service interfaces. Must not depend on Infrastructure or WebApi.
- **Application:** MediatR commands/queries/handlers, FluentValidation validators, pipeline behaviors, `Ardalis.Result`. One folder per use case.
- **Infrastructure:** `ApplicationDbContext`, EF configurations, store implementations, JWT, encryption, MinIO, Stripe, Redis, SMTP, Polly, hosted services.
- **WebApi:** Controllers, auth/authorization, routing, middleware, rate limiting, OpenAPI. Controllers must not contain business rules or direct persistence logic.

**Key deviation from textbook Clean Architecture:** Infrastructure depends on Domain only (not Application). Service interfaces live in `Domain/Abstractions/Services/`.

### Commerce flow

```
CheckoutIntent → CheckoutIntentItem + CheckoutReservation
                    ↓ (Stripe webhook only)
Order → OrderLine → Purchase (per-asset entitlement)
```

- Stripe webhook is the **sole payment-completion authority** — browser redirects never create entitlements
- `Purchase` is the entitlement used by library, downloads, and reviews
- All webhook fulfillment is idempotent

### Asset storage

- AES-256-GCM encrypted before upload to MinIO
- Chunked streaming: 1 MiB chunks, per-chunk nonce + AAD for reorder protection
- Storage key built server-side: `assets/{authorId}/{assetId}/{versionId}{ext}`
- Download: author gets any version; purchaser gets purchased version + any higher version

### Conditional delete

- Assets with purchases/active checkout → **soft delete** (blobs retained for buyers)
- Assets without purchases → **hard delete** with blob cleanup via outbox

### Email & verification

- Email verification gates marketplace writes (upload, checkout, reviews, profile edits, admin APIs)
- Recovery flows, catalog reads, and owned-asset downloads remain available while unverified
- Action links use ASP.NET Core Data Protection (tamper-evident, time-limited) — no tokens in DB
- `VERIFIED_EMAIL` policy reads `EmailVerifiedAt` from database on each request (not JWT claim)

### Authorization layers

1. **Roles:** `ADMIN` / `USER` via `[Authorize(Roles = ...)]`
2. **Verified email policy:** Required for marketplace writes — no admin bypass

## Key patterns

- **Use cases:** Sealed command/query records, internal sealed handlers, co-located validators
- **Results:** `Ardalis.Result` with `ResultError.Error("ERR_*")` for validation-style errors; native `NotFound`/`Forbidden`/`Conflict` for those statuses
- **Error codes:** Every `ERR_*` requires entry in `ErrorCodesToErrorMessages.cs`
- **Transactions:** Short; never place Stripe/MinIO/cache/SignalR inside open transaction
- **Concurrency:** Unique indexes, conditional updates, row locking, explicit idempotency
- **Cache:** Use `CacheKeys`, explicit TTLs, invalidation after writes

## Tests

- **Unit tests:** `AssetBlock.*.Tests` — xUnit + NSubstitute + FluentAssertions, no Docker
- **Integration tests:** `AssetBlock.Infrastructure.IntegrationTests` and `AssetBlock.WebApi.IntegrationTests` — Testcontainers (PostgreSQL/Redis), requires Docker
- **Naming:** `Handle_When<Condition>_Should<Expected>` or `Validate_When<Condition>_Should<Expected>`

## Secrets

- Tracked `appsettings*.json` contain placeholders only
- Use .NET User Secrets or gitignored `appsettings.Development.json` for local config
- Never commit JWT keys, AES keys, Stripe secrets, or real credentials

## Additional guidance

For detailed backend coding conventions, see `asblock-backend/AGENTS.md`.
