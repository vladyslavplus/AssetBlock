---
name: assetblock-backend
description: >-
  Backend conventions for the AssetBlock .NET solution (AssetBlock.Domain,
  Application, Infrastructure, WebApi): layering, Ardalis.Result, MediatR, stores,
  API routes, caching, errors, and code style. Use when editing, reviewing, or
  adding features in asblock-backend, or when the user asks about this API codebase.
---

# AssetBlock backend conventions

## Solution layout

- **Domain**: entities, DTOs, abstractions (`I*Store`), domain exceptions, `ErrorCodes`, `ErrorCodesToErrorMessages`, `CacheKeys`, other constants. No infrastructure references.
- **Application**: MediatR commands/queries + handlers, FluentValidation validators, `ResultError`, pipeline behaviors (`LoggingBehavior`, `ValidationBehavior`). Depends on Domain + MediatR + FluentValidation + Ardalis.Result.
- **Infrastructure**: EF Core `ApplicationDbContext`, entity configurations, store implementations, external services (JWT, MinIO, Redis cache, Stripe, Elasticsearch), Polly pipelines, hosted services.
- **WebApi**: controllers inheriting `ApiControllerBase`, extensions (JWT, Swagger, rate limiting, exception handler), `ApiRoutes` and other API constants.

## Naming: avoid redundant `Async` suffix

- **Application and Domain**: name store and domain methods by intent (`GetById`, `Create`, `Update`, `Delete`), **not** `GetByIdAsync`, unless the codebase already uses a suffix for a specific type (rare).
- **Framework APIs**: keep BCL/EF names as-is (`SaveChangesAsync`, `FirstOrDefaultAsync`, `Send` on `ISender`, etc.).

## Braces and control flow

- Use **always** a braced block for `if`, `else`, `foreach`, `for`, `while` (even single-line bodies). No single-line `if` without `{ }`.
- Prefer early returns in handlers to limit nesting.

## Ardalis.Result

- Handlers return `Result<T>` or `Result`.
- **Validation-style errors with a registered message**: use `ResultError.Error<T>(ErrorCodes.ERR_XXX)` or `ResultError.Error(ErrorCodes.ERR_XXX)` so messages resolve via `ErrorCodesToErrorMessages`.
- **Not found / forbidden / conflict**: use `Result.NotFound(ErrorCodes.…)`, `Result.Forbidden(…)`, `Result.Conflict(…)` with an **error code string** (see `ApiControllerBase.MapResultToActionResult`).
- Success: `Result.Success(value)` or `Result.Success()`.
- Add **every new** `ErrorCodes` constant to `ErrorCodesToErrorMessages` with a clear English message. Prefer consistent `ERR_*` naming; avoid mixing styles unless matching existing codes in the same feature area.

## Constants (do not scatter magic strings)

- **`private const` fields** in validators and other classes: **ALL_UPPER** with underscores (e.g. `MAX_LINKS`, `MAX_URL_LENGTH`). This matches `.editorconfig` rule `constant_fields_should_be_all_upper` (IDE naming). Do not use PascalCase like `MaxLinks` for `const` fields.
- **HTTP route segments**: `AssetBlock.WebApi.Constants.ApiRoutes` (nested static classes per area).
- **API error identifiers**: `ErrorCodes` + `ErrorCodesToErrorMessages`.
- **Cache keys**: `CacheKeys` in Domain; use helpers for parameterized keys (e.g. list invalidation prefixes).
- **Rate limiting**: `RateLimitingConstants` (policies + window sizes); reference policies from `[EnableRateLimiting(...)]`.
- **Roles / JWT claim types**: `AppRoles`, `JwtClaimTypes` (and related) in Domain constants.
- **Resilience / Polly**: `ResilienceConstants` where applicable.

## MediatR

- Register with `RegisterServicesFromAssembly` (see `Application.DependencyInjection`).
- Handlers: `internal sealed class XxxHandler(...) : IRequestHandler<TRequest, TResponse>`.
- Commands/queries: `sealed record` in a folder per use case (e.g. `UseCases/Auth/Login/`).
- Controllers map DTOs to commands/queries and call `Sender.Send(...)`, then `MapResultToActionResult`.

## FluentValidation

- Validators are registered via `AddValidatorsFromAssembly`; co-locate `*Validator` with the command/query or under `Validators/` — follow existing neighbors in the same feature.
- Failed validation throws `ValidationException`; `UseValidationExceptionHandler` maps it to 400 problem+json.
- **Nullable reference types**: if you use `When(c => c.SomeProp is not null, ...)`, ensure the command property is **nullable** in the type (`List<T>?`, `string?`, etc.) when the value can actually be absent (e.g. JSON null). If the property is non-nullable, the compiler correctly reports that the `is not null` check is always true—fix the model, not by suppressing warnings.
- **Do not** use `ArgumentNullException.ThrowIfNull` (or similar) in handlers for inputs already validated by FluentValidation; `ValidationBehavior` runs before the handler. Where the type is still nullable for NRT, use the null-forgiving operator (`request.Links!`) when the validator guarantees the value.

## Web API controllers

- Inherit `ApiControllerBase(ISender sender)`; use `Sender` to dispatch MediatR.
- Use `[ApiVersion("1.0")]` from base; route templates use `ApiRoutes` constants.
- Protect endpoints with `[Authorize]` where required; use `GetUserId()` from base for the current user id.
- Apply `[EnableRateLimiting(RateLimitingConstants.Policies.…)]` on sensitive or abuse-prone endpoints (auth, uploads, downloads, payments), consistent with existing controllers.

## Infrastructure stores (`I*Store` implementations)

- Implementations are typically `internal sealed class XxxStore(...)` in `Persistence/Stores/`.
- Use EF Core with `AsNoTracking()` for read-only queries where appropriate.
- **PostgreSQL unique violations**: catch `DbUpdateException` with `PostgresException` / `PostgresErrorCodes.UniqueViolation` and throw narrow **domain exceptions** (e.g. `DuplicateEmailException`) or handle in the handler — see `UserStore` and `RegisterCommandHandler`.
- Prefer translating infrastructure failures to **Result** in the handler when the failure is part of the business contract.

## try / catch

- Use **infrastructure** code when external systems may fail and you need to log and degrade or rethrow (e.g. Redis `ICacheService`: log warnings, avoid crashing the request for cache miss/write failures; rethrow `OperationCanceledException`).
- Use in **handlers** when catching **domain exceptions** from stores and converting to `Result` (validation-style or conflict).
- Do not blanket-catch without logging or without rethrowing unexpected errors; avoid swallowing exceptions silently.

## Caching

- Inject `ICacheService`; keys from `CacheKeys` only.
- When introducing new cached lists, follow existing TTL and invalidation patterns (prefix removal where applicable).

## EF Core migrations

- **Do not hand-edit or hand-author migration files.** Generate with `dotnet ef migrations add ...` only when the user approves. After schema changes, update snapshots via the tool, not by copying Designer files manually.

## Exceptions and HTTP

- `UseValidationExceptionHandler` handles `ValidationException` → 400. Other unhandled exceptions → 500 problem details.
- Domain-specific HTTP semantics should be expressed via **Result** in handlers so controllers return 404/403/409 through `MapResultToActionResult`.

## Code style (match existing code)

- File-scoped namespaces; primary constructors for handlers/services where already used.
- English identifiers and comments only; **no non-ASCII characters in source** (no Cyrillic/emojis in code, strings committed as product literals, or comments).
- Keep comments short; explain non-obvious **why**, not restating the code.
- Dependencies: register in `Infrastructure.DependencyInjection` or `Application.DependencyInjection` as appropriate; stores are usually **scoped**.

## Testing

- See the dedicated skill [assetblock-backend-testing](../assetblock-backend-testing/SKILL.md) (xUnit, **NSubstitute**, FluentAssertions). This repo does not use Moq.

## Additional resources

- [reference.md](reference.md) — compact handler and controller snippets.
- [assetblock-backend-testing](../assetblock-backend-testing/SKILL.md) — test project layout and Result assertions.

## Quick checklist for a new feature

1. Domain: entities/DTOs/errors/codes + `ErrorCodesToErrorMessages` entries.
2. Application: command/query + handler + FluentValidation where inputs need rules.
3. Infrastructure: store methods + EF configuration if needed; wire DI.
4. WebApi: `ApiRoutes` + controller actions + auth/rate limits as needed.
5. Migrations: only via EF CLI after explicit approval.

## Related project rules

- See `.cursor/rules/*.mdc` in this repo (`dotnet-best-practices`, `security`, `errors`, `core-workflow`, etc.) for additional constraints.
