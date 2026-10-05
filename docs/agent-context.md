# Agent Context Map

This is a navigation map, not a copy of implementation or a completion ledger. Paths are repository-relative. Read only the row relevant to the task, then confirm current code and callers. Update this map when a mapped entry point moves or a trust boundary changes.

| Concern | Backend entry and implementation | Frontend entry | Invariant / focused evidence |
| --- | --- | --- | --- |
| Authentication/session | `asblock-backend/AssetBlock.WebApi/Controllers/AuthController.cs`; `AssetBlock.Application/UseCases/Auth/` | `asblock-frontend/app/api/auth/`; `lib/server/refresh-session.ts`, `backend-authorized.ts`, `auth-cookies.ts` | Tokens remain behind BFF cookies; inspect auth/session helper tests and matching WebApi tests. |
| Catalog/detail/search | `AssetsController.cs`; `UseCases/Assets/GetAssets/`; `AssetBlock.Infrastructure/Persistence/Stores/AssetStore.cs` | `app/assets/`; `lib/catalog/` | Public visibility requires nondeleted/current READY; exact lexical pagination and bounded relevance have different contracts. |
| Upload/version processing | `AssetsController.cs`, `UsersController.cs`; `UseCases/Assets/UploadAsset/`, `PublishAssetVersion/`; `Infrastructure/HostedServices/AssetProcessing/` | `app/sell/`; `components/sell/`; authenticated seller BFF routes | Encrypt before storage; processing precedes public READY promotion; preserve previous READY version. |
| Copilot | `UseCases/Assets/EnqueueListingCopilot/`; `Infrastructure/HostedServices/AssetProcessing/Handlers/ListingCopilotJobHandler.cs` | `components/sell/listing-copilot-panel.tsx` | Sanitized bounded inputs, taxonomy allowlist, selective apply; no automatic save/publication. |
| Checkout/entitlements | `PaymentsController.cs`; `UseCases/Payments/`; `Infrastructure/Services/StripePaymentService.cs` | `app/api/payments/`; checkout pages; `lib/payments/` | Verified webhook is authoritative; idempotent orders/purchases; network I/O outside DB transactions. |
| Library/download/deletion | `UsersController.cs`, `AssetsController.cs`; `Infrastructure/Services/DownloadService.cs`; `UseCases/Assets/DeleteAsset/` | `app/library/`; `lib/library/`; `components/library/` | Ownership/entitlement before delivery; preserve buyer blobs and active checkout references. |
| Recommendations | `UseCases/Assets/GetSimilarAssets/`, `GetPersonalSimilarAssets/`; `Stores/AssetStore.cs`, `RecommendationPersonalizationStore.cs` | `components/assets/similar-assets-block.tsx`; `lib/catalog/asset-detail-query.ts`; `lib/analytics/` | Eligibility first; opt-in personalization; cross-user isolation; explanations never rank. |
| Analytics | `SellerAnalyticsController.cs`, `AnalyticsController.cs`; `UseCases/Analytics/`, `UseCases/SellerAnalytics/` | `app/api/seller/analytics/`; `components/sell/analytics/` | Orders/lines define commerce; telemetry is optional and respects DNT/GPC; ranges are UTC. |
| Collections/bundles | `CollectionsController.cs`, `BundlesController.cs`, seller controllers; `UseCases/Collections/`, `UseCases/Bundles/` | `app/collections/`, `app/bundles/`; seller components/routes | Collections are editorial; bundle revisions and checkout snapshots preserve purchased terms. |
| Reviews/admin/audit | `ReviewsController.cs`, `AuditLogsController.cs`, `AdminUsersController.cs`, `ModerationSubmissionsController.cs`; matching use cases | `app/admin/`; feature BFF routes/components | Purchaser eligibility, live persisted roles (JWT refresh), Admin User/Moderator assignment, Moderator-only case summary reads; safe audit metadata. |

Backend paths after the first row are relative to `asblock-backend/AssetBlock.WebApi/Controllers/`, `AssetBlock.Application/`, or `AssetBlock.Infrastructure/` as indicated. Search exact symbols when a feature folder differs; never infer absence from this abbreviated map.

Backend tests mirror production areas under `AssetBlock.Application.Tests`, `AssetBlock.Infrastructure.Tests`, `AssetBlock.Infrastructure.IntegrationTests`, `AssetBlock.WebApi.Tests`, and `AssetBlock.WebApi.IntegrationTests`. Frontend tests are feature-local `.test.ts`/`.test.tsx`; browser flows are in `asblock-frontend/e2e/`. Test existence is not passing evidence.

## Isolated code analysis tooling

`scripts/code_analysis/README.md` documents the corpus/extraction/baseline CLI and
explicit parent-run, reviewed-label, and sandbox inputs. Local source snapshots,
review records, environments, vectors, and measured evidence stay under ignored
`artifacts/code_analysis/`. This tooling has no product publication authority;
unit tests do not require historical runs. `scripts/feasibility_pilot/paths.py`
provides shared lexical link/reparse rejection and resolved containment checks.

## Retrieval order

1. Read root and applicable nested instructions once; choose the relevant feature above.
2. Use Serena symbol overview/search/references when available; request bodies only for relevant symbols. Use `rg` for routes, JSON, configuration, SQL, dynamic dispatch, or missing symbols.
3. Follow caller, contract, persistence/ownership boundary, BFF, and focused tests. Symbol references alone do not prove HTTP/DI/data-flow alignment.
4. Record unresolved boundaries and source provenance in the handoff. Do not paste whole files or rescan already established unrelated areas.

For local tool setup and bounded handoff packages, see [agent-tools.md](agent-tools.md).
