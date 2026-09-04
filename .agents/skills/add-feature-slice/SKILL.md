---
name: add-feature-slice
description: Implement a complete end-to-end vertical feature slice across backend and frontend in AssetBlock. Use when adding or expanding a vertical feature spanning API, persistence, BFF, and UI.
---

# Add Feature Slice

This skill coordinates the vertical touchpoints required to implement an end-to-end feature slice across the AssetBlock monorepo.

## 1. Golden References

Before designing a new slice, consult the maintained golden references:

- **Backend:**
  - Command / Write: `PublishAssetVersion` (see [backend guide](../../asblock-backend/AGENTS.md#golden-references))
  - Read / Query: `GetAssets` (see [backend guide](../../asblock-backend/AGENTS.md#golden-references))
- **Frontend:**
  - Feature Slice: `Library` (`lib/library/`, `app/library/`, `components/library/`, see [frontend guide](../../asblock-frontend/AGENTS.md#golden-references))
  - Authenticated BFF Route: `app/api/account/library/route.ts`

## 2. Backend Touchpoints

Implement the backend slice in dependency order:

1. **Domain & Contracts:**
   - Define domain entities/DTOs in `AssetBlock.Domain/Core/`.
   - Add new `ERR_*` codes to `ErrorCodes` and map them in `ErrorCodesToErrorMessages`.
   - Define method contracts on the appropriate store interface (`I*Store`) in `AssetBlock.Domain/Abstractions/Services/`.
2. **Application Use Case:**
   - Create a dedicated folder under `AssetBlock.Application/UseCases/<Area>/<UseCaseName>/`.
   - Define sealed Command/Query record.
   - Define co-located `*Validator` inheriting `AbstractValidator<T>`.
   - Implement sealed handler returning `Result<T>` or `Result`.
   - Use `IUnitOfWork` for transactional writes, `IAuditWriter` for audit events, and `TimeProvider` for timestamps.
3. **Persistence (Infrastructure):**
   - Implement store methods in `AssetBlock.Infrastructure/Persistence/Stores/`.
   - If schema changes are required, obtain explicit approval and follow [.agents/skills/add-migration/SKILL.md](../add-migration/SKILL.md).
4. **WebApi Transport:**
   - Expose endpoint in appropriate controller inheriting `ApiControllerBase`.
   - Apply `[Authorize]` and `[EnableRateLimiting]` as appropriate.
   - Map mediator results via `MapResultToActionResult`.

## 3. Frontend Touchpoints

Implement the client-side slice matching backend contracts:

1. **Schemas & Types:**
   - Define Zod schema and TypeScript inferred types in `lib/<feature>/<feature>-schemas.ts`.
2. **BFF Route Handler:**
   - Create `app/api/<feature>/route.ts`.
   - Validate input via Zod schema using `zodValidationProblemResponse`.
   - Forward request using `proxyAuthenticatedBff` or `proxyAnonymousBff` from `@/lib/server/bff-route`.
3. **Query & Mutation Hooks:**
   - Define or reuse feature query keys directly in the feature's `lib/<feature>/<feature>-query.ts` (e.g. `<feature>Keys = { all: ['<feature>'] as const, ... }`), following the Golden Reference (`lib/library/library-query.ts`).
   - Implement TanStack Query hooks or query options (`useQuery` / `useMutation`) in `lib/<feature>/<feature>-query.ts`.
   - Manage optimistic updates or query invalidation on mutation success.
4. **UI Components & Pages:**
   - Create route page in `app/<feature>/page.tsx` with descriptive metadata.
   - Build client components in `components/<feature>/` adhering to design tokens, dark theme, and Lucide icons.
   - For mutations, use React Hook Form with `zodResolver` and display user feedback via Sonner toast.

## 4. Cross-Stack Contract Alignment

Keep contracts strictly synchronized:
- Wire casing: camelCase JSON across both backend and frontend.
- Error codes: Frontend must parse and match the backend `ERR_*` identifiers.
- Pagination: Standard `{ page, pageSize, total, items }` contract.
- Status codes: 200 OK, 201 Created, 400 Validation Problem, 401 Unauthorized, 403 Forbidden, 404 Not Found, 409 Conflict.

## 5. Verification

Execute focused verification on both sides:
1. Backend: `dotnet test asblock-backend/AssetBlock.Application.Tests --filter "FullyQualifiedName~<Feature>"`
2. Frontend: `pnpm --dir asblock-frontend run check && pnpm --dir asblock-frontend run test:unit <feature>`
3. Build check: `pnpm --dir asblock-frontend run build` and `dotnet build asblock-backend/asblock-backend.slnx`
