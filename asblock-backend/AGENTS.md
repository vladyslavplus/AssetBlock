# AssetBlock Backend Agent Guide

## Project and architecture

- This is the canonical guide for `asblock-backend/`: .NET 10, ASP.NET Core controller-based Web API, PostgreSQL/EF Core, MediatR, FluentValidation, Ardalis.Result, Redis, MinIO, Stripe, SignalR, Serilog, and Polly.
- Preserve the existing dependency direction: `Domain -> Application -> Infrastructure -> WebApi`.
- `Domain` intentionally contains entities, DTOs, options, constants, and `I*Store`/service abstractions. Do not move them for textbook Clean Architecture purity.
- Do not modify `DatabaseMigrationService` during ordinary work. Its migration and demo-seeding behavior is intentional.
- Do not change project references, layer boundaries, API routes, configuration keys, database schema, or public payloads unless the task explicitly requires it.
- Read relevant code and tests first. Keep diffs focused, preserve user changes, and avoid unrelated refactors, speculative abstractions, or broad renames.

## Layer responsibilities

- **Domain:** entities, DTOs, options, constants, error codes/messages, cache keys, domain exceptions, and interfaces. It must not depend on Infrastructure or WebApi.
- **Application:** MediatR commands/queries, handlers, FluentValidation validators, pipeline behaviors, `ResultError`, and business orchestration. Keep each use case in its own folder.
- **Infrastructure:** `ApplicationDbContext`, EF configurations, `I*Store` implementations, JWT, password hashing, MinIO, encryption, cache, Stripe, Polly, hosted services, and DI registrations.
- **WebApi:** controllers, request binding, auth/authorization, routing, middleware, OpenAPI, rate limits, exception-to-HTTP mapping, and startup. Controllers must not contain business rules or direct persistence logic.
- Register implementations in the layer that owns them: application mechanics in `AddApplication`, integrations/stores/hosted services in `AddInfrastructure`, transport concerns in WebApi.

## Business invariants

- Assets are encrypted before storage. A purchaser or the asset author may download; all other users must be denied.
- Asset deletion is conditional: assets with purchases are delisted by soft delete and their blobs remain available to existing purchasers; assets without purchases may be hard deleted with blob cleanup.
- Stripe webhook verification, not a browser redirect, is the source of truth for payment completion. Purchase creation must be idempotent.
- Reviews are limited to eligible purchasers and follow the existing ownership/time-window rules.
- Categories, tags, and review moderation are admin-controlled. Do not weaken role checks.
- JWT access/refresh flows, refresh-token revocation, SignalR notifications, cache invalidation, and catalog search are cross-cutting behaviors; update their tests when changing the related feature.

## Application, validation, and API behavior

- Use sealed command/query records, internal sealed handlers, and co-located `*Validator` classes. Follow neighboring use-case structure.
- Controllers bind input, apply endpoint authorization/rate limits, build a command/query, call `Sender.Send`, and map the result.
- Use `Ardalis.Result` for expected business outcomes. Use `ResultError.Error(...)` for validation-style `ERR_*` codes and native `NotFound`, `Forbidden`, and `Conflict` Results for those statuses.
- Every new `ERR_*` identifier requires a clear entry in `ErrorCodesToErrorMessages`. Keep error identifiers, cache keys, routes, roles, JWT claims, rate-limit policies, and resilience names in existing constant classes.
- FluentValidation is the normal input validation path. Do not add duplicate null guards in handlers for values guaranteed by validators.
- Preserve the active API error contract. If a planned change alters it, change all sources together: Result mapping, model binding, exception handling, JWT challenge/forbid, rate-limit rejection, frontend parsing, OpenAPI, and tests.
- Apply `[Authorize]` and `[EnableRateLimiting]` at sensitive endpoint boundaries. Pass `CancellationToken` through MediatR, EF Core, streams, and external I/O.

## Persistence, transactions, and concurrency

- Prefer existing `I*Store` abstractions and implementations. Do not create a second repository/service layer without a concrete need.
- Use async EF Core APIs and `AsNoTracking()` for read-only queries where appropriate. Project only required fields; avoid loading entity graphs or creating N+1 queries without a reason.
- Use pagination for list endpoints, deterministic ordering for paged results, and database-side filtering/sorting rather than in-memory work.
- Translate known PostgreSQL unique violations narrowly into business outcomes; never swallow unexpected `DbUpdateException` failures.
- Keep transactions short. Never place Stripe, MinIO, cache network calls, SignalR delivery, or slow file work inside an open transaction.
- For concurrent writes, enforce invariants with unique indexes, conditional updates, row locking, or explicit idempotency. Do not rely on a prior read alone when a race can produce duplicate effects.
- Generate migrations only via `dotnet ef migrations add ...` after explicit user approval. Never hand-edit migration files, designer files, or the model snapshot.

## Files, external services, cache, and performance

- Treat upload/download, encryption, MinIO, Stripe, Redis, SignalR, and catalog query paths as failure-prone I/O boundaries. Use the existing Polly and logging patterns where meaningful.
- Preserve AES-GCM chunk integrity rules: fresh nonce per chunk, authenticated data ordering, and end-of-stream validation. Never expose plaintext blobs or encryption keys.
- When changing file handling, avoid buffering an entire payload in memory or persisting plaintext to disk unless the requirement explicitly accepts that trade-off. Preserve cancellation and clean up partial/orphaned objects safely.
- Build MinIO object keys server-side; do not trust client filenames as paths. Keep download authorization before content delivery.
- Cache only safely reusable data. Use `CacheKeys`, explicit TTLs, and invalidation after committed writes. Cache failures may degrade only when stale/missed data is acceptable.
- Keep catalog search derived from PostgreSQL state (FTS + pg_trgm). Do not silently return an empty catalog when a database failure must be visible.
- Use `IHttpClientFactory` for new HTTP integrations. Reuse existing configured clients/pipelines before adding a package or retry mechanism.

## Security and observability

- Never commit, log, return, or copy secrets, JWTs, refresh tokens, passwords, encryption keys, Stripe webhook payloads, or private storage credentials.
- Use configuration/options and secure secret sources; validate required options at startup. Do not add fallback production credentials.
- Treat CORS, cookies, JWT validation, SignalR query tokens, rate limits, upload limits, webhook signatures, and authorization ownership checks as high-risk changes.
- Use parameterized EF Core/Dapper access only. Treat all request data, claims, browser state, storage metadata, and external webhook payloads as untrusted until validated.
- Use structured `ILogger<T>`/Serilog logs with safe identifiers and exception objects. Do not log complete request/response bodies by default.
- Catch exceptions only to recover, translate a known business/infrastructure failure, retry, or add useful context before rethrowing. Do not blanket-catch and hide failures.

## Code style

- Follow `.editorconfig`, nullable reference types, file-scoped namespaces, and nearby primary-constructor usage.
- Use braces for all control flow and early returns to limit nesting.
- Private fields use `_camelCase`; `const` fields and Domain enum members use `ALL_UPPER_SNAKE_CASE`; enum type names remain PascalCase.
- Do not add redundant `Async` suffixes to project methods. Keep framework method names unchanged.
- Keep methods focused, comments concise, and comments limited to non-obvious decisions. Do not use `<c>` in XML documentation.
- Add packages only when platform capabilities or existing dependencies cannot solve the need. Prefer stable compatible versions and remove unused direct references.

## Tests and verification

- Use xUnit, NSubstitute, FluentAssertions, and `NullLogger<T>`; do not add Moq.
- Prefer one meaningful integration test against the real application pipeline and PostgreSQL behavior over several mock-heavy unit tests that only restate implementation details.
- Do not add SQLite or EF Core InMemory fallbacks to production code solely to make tests pass. PostgreSQL-specific queries, constraints, transactions, locking, JSON, and search behavior must be covered by integration tests against PostgreSQL.
- Mirror application structure in tests. Name tests `Handle_When<Condition>_Should<Expected>` or `Validate_When<Condition>_Should<Expected>`.
- Cover happy paths plus validation, not-found, authorization, conflict, external failure, cancellation, and idempotency when relevant. Assert store/cache/event interactions with `Received` and `DidNotReceive`.
- Test handlers and validators first; add WebApi/integration tests when HTTP pipeline, auth, configuration, persistence, or DI behavior changes.
- Run the narrowest affected test project first. For cross-cutting backend changes, run `dotnet test asblock-backend.slnx`; run `dotnet build asblock-backend.slnx` when the full solution has restored assets.
- If verification cannot run, state precisely what was not verified and why.
