# AssetBlock Improvement Audit

Read-only audit of the whole monorepo: code smells, refactor opportunities, security issues, SQL/LINQ performance, duplication, hardcoded values, tooling, and the agent workflow itself.

Written in English on purpose: every finding references English identifiers, and root `AGENTS.md` requires code and contracts to stay English. This file is intentionally *not* gitignored — it is meant to be committed and worked through.

## How to read this

Each finding has a fixed shape, modelled on `.cursor/agents/backend-reviewer.md` / `.cursor/agents/frontend-reviewer.md`:

- **Where** — `path:line`, every location when the pattern repeats.
- **Severity** — `Critical` / `High` / `Medium` / `Low` / `Nit`.
- **Evidence** — what the code actually does.
- **Fix** — one concrete, prescriptive action.

Severity is calibrated for *this* project: a pet/academic marketplace that the author wants to hold to professional production standards. It is **not** calibrated for "there is real money and real user data in production today". Where a finding only matters after deployment, it says so.

Findings the roadmap in `assetblock.md` already defers on purpose (trusted forwarded headers, HSTS/CSP, production Dockerfiles, distributed rate limits, API versioning, centralized logs) are excluded unless there is a concrete reason to pull them forward — in which case the finding says why.

Sections:

- [A. Backend — Domain & Application](#a-backend--domain--application)
- [B. Backend — Persistence, SQL & LINQ](#b-backend--persistence-sql--linq)
- [C. Backend — WebApi & security](#c-backend--webapi--security)
- [D. Backend — Integrations, workers & crypto](#d-backend--integrations-workers--crypto)
- [E. Frontend — BFF, auth & contracts](#e-frontend--bff-auth--contracts)
- [F. Frontend — UI, state, performance & a11y](#f-frontend--ui-state-performance--a11y)
- [G. Build, CI & repository hygiene](#g-build-ci--repository-hygiene)
- [H. Agent workflow](#h-agent-workflow)
- [I. Suggested order of work](#i-suggested-order-of-work)

## What is already good

Worth stating explicitly, because these are the parts that should *not* be "improved" and because several of them are better than what most commercial codebases do:

- **Zero `TODO` / `FIXME` / `HACK` / `XXX` markers** in the entire `.cs` / `.ts` / `.tsx` surface. The only match is the word "Use with" in a doc comment. That is unusual and worth protecting.
- **Options validation is genuinely thorough.** `OptionsValidation.IsMissingOrPlaceholder` (`asblock-backend/AssetBlock.Infrastructure/Options/OptionsValidation.cs:9`) rejects tracked-config placeholders like `<stripe-secret-key>`, so the placeholder JWT key in `appsettings.json:14` cannot boot a real app even though it is 46 characters long and would pass a naive length check. 21 option validators exist. This is the correct pattern.
- **No real secrets in tracked config.** `asblock-backend/AssetBlock.WebApi/appsettings.json` is entirely placeholders, and Gitleaks runs on every push and PR.
- **Dependency governance is ahead of the curve** for a project this size: `dependency-policy.json`, reviewed `dependency-exceptions.json`, generated `THIRD-PARTY-NOTICES.md`, CycloneDX SBOMs, and a CI gate that has its own unit tests (`pnpm deps:test`).
- **The review execution boundary is explicit and mechanically enforced.** Root `AGENTS.md:23-26` forbids reviewers from running commands, and `.cursor/agents/backend-reviewer.md:9` backs it with `readonly: true` rather than trusting prose. Most repos get this wrong.
- **`TimeProvider` is already registered** (`AssetBlock.Infrastructure/DependencyInjection.cs:84`) — the abstraction exists, it is just under-used (see [D](#d-backend--integrations-workers--crypto)).
- **Integration tests target real PostgreSQL** via Testcontainers rather than SQLite/InMemory, which root guidance explicitly forbids. Storage providers share one contract test suite.

---

## A. Backend — Domain & Application

### A1. Stripe webhook throws raw exceptions instead of returning a Result

- **Where:** `AssetBlock.Application/UseCases/Payments/HandleStripeWebhook/HandleStripeWebhookCommandHandler.cs:99`, `:109`, `:122`
- **Severity:** High
- **Evidence:** On a paid-checkout or intent mismatch the handler logs and then `throw new InvalidOperationException(...)`, bypassing `Ardalis.Result` entirely. The exception surfaces as a 500, which Stripe treats as a delivery failure and retries — indefinitely, for an event that will never succeed.
- **Fix:** Add `ERR_PAYMENT_WEBHOOK_MISMATCH` to `ErrorCodes` + `ErrorCodesToErrorMessages` and return `Result.Error(...)`, mapped to a 200-with-logged-anomaly so Stripe stops retrying a permanently unprocessable event. Add one handler test per mismatch branch.

### A2. Upload and publish duplicate the entire encrypt-and-upload pipeline

- **Where:** `AssetBlock.Application/UseCases/Assets/UploadAsset/UploadAssetCommandHandler.cs:207-265`, `AssetBlock.Application/UseCases/Assets/PublishAssetVersion/PublishAssetVersionCommandHandler.cs:192-249`
- **Severity:** High
- **Evidence:** `EncryptAndUpload` is copied verbatim — same `Pipe`, same dual `Task.Run`, same hash finalization, same `PlaintextHashObservingStream`. This is the single most security-sensitive code path in the project, and it exists twice, so any fix to nonce handling, cleanup, or cancellation has to be applied twice and can silently diverge.
- **Fix:** Extract `AssetEncryptUploadService` into `Application/Common`, move `PlaintextHashObservingStream` with it, and have both handlers call it. This is the highest-value refactor in the backend.

### A3. `Task.Run` in the encrypt pipeline discards the cancellation token

- **Where:** `AssetBlock.Application/UseCases/Assets/UploadAsset/UploadAssetCommandHandler.cs:220-234`, `AssetBlock.Application/UseCases/Assets/PublishAssetVersion/PublishAssetVersionCommandHandler.cs:204-218`
- **Severity:** Medium
- **Evidence:** `Task.Run(..., CancellationToken.None)` on the encryption leg while the upload leg honours `cancellationToken`. An aborted upload leaves the encryption task running to completion against a pipe nobody reads.
- **Fix:** Pass `cancellationToken` to both legs inside the extracted service from A2, or use an explicit linked CTS and document why the encrypt leg must outlive the request.

### A4. Handlers re-validate file size and extension already covered by validators

- **Where:** `UploadAsset/UploadAssetCommandHandler.cs:35-54`, `PublishAssetVersion/PublishAssetVersionCommandHandler.cs:34-53`, against `UploadAssetCommandValidator.cs:38-39` and `PublishAssetVersionCommandValidator.cs:29-30`
- **Severity:** Medium
- **Evidence:** Both handlers re-check `FileLength`, extension, and license despite the validators already enforcing them. `asblock-backend/AGENTS.md:35` explicitly forbids this ("Do not add duplicate null guards in handlers for values guaranteed by validators"). Two enforcement points for one limit means they can disagree.
- **Fix:** Delete the handler guards; keep limits in the validators and `FileUploadOptions` only. Move the corresponding size/extension cases in `UploadAssetCommandHandlerTests` onto the validator tests.

### A5. Seller-analytics date-range rules exist as a shared helper that two validators ignore

- **Where:** `UseCases/SellerAnalytics/SellerAnalyticsRangeRules.cs:8-47`; `GetSellerAnalyticsOverview/GetSellerAnalyticsOverviewQueryValidator.cs:10-41`; `GetSellerAnalyticsSales/GetSellerAnalyticsSalesQueryValidator.cs:10-21`; `GetSellerAnalyticsProducts/GetSellerAnalyticsProductsQueryValidator.cs:11-22`
- **Severity:** High
- **Evidence:** `SellerAnalyticsRangeRules` was written to centralize these rules, but Overview re-inlines them and Sales/Products omit the min-range and comparison-period rules that Overview has. So the same conceptual "valid analytics range" is enforced three different ways, and two of them are weaker. `README.md:64` documents one contract ("`from` inclusive, `to` exclusive, max 366 days") that the code does not uniformly apply.
- **Fix:** Call `SellerAnalyticsRangeRules.ApplyDateRangeRules` from all three validators and delete the inlined copies. Add validator tests for the constraints Sales/Products currently miss.

### A6. Commands and queries missing validators entirely

- **Where:** `Users/GetMyListings/GetMyListingsQuery.cs:7`, `Tags/GetTags/GetTagsQuery.cs:7`, `Assets/GetAssetById/GetAssetByIdQuery.cs`, `Assets/GetAssetVersions/GetAssetVersionsQuery.cs:7`, `Users/MarkNotificationRead/MarkNotificationReadCommand.cs:6`, `Reviews/DeleteReview/DeleteReviewCommand.cs:6`, `Payments/GetCheckoutStatus/GetCheckoutStatusQuery.cs:6`, `SellerAnalytics/ExportSellerAnalyticsSales/ExportSellerAnalyticsSalesCommand.cs:8-14`, `Tags/DeleteTag/`, `Categories/DeleteCategory/`
- **Severity:** Medium
- **Evidence:** Ten use cases have no co-located `*Validator`. Two matter more than the rest: `GetMyListingsQuery` and `GetTagsQuery` both embed a `PagedRequest` with no `PagedRequestValidator`, so page size reaches the store unbounded — and `NotificationStore` is confirmed to have no clamp of its own (see [B12](#b12-pagination-lacks-a-deterministic-tie-breaker-in-four-stores)). `ExportSellerAnalyticsSalesCommand` carries `SellerId`, `From`, `To`, and `ProductType` with only `Session.ExceedsMax` checked in the handler.
- **Fix:** Add validators for all ten. Start with the two paged queries (`SetValidator(new PagedRequestValidator())`) and the export command (mirror `PrepareSellerAnalyticsSalesExportQueryValidator`); the rest are one-line `NotEmpty()` guards on ids.

### A7. `HandleStripeWebhookCommandHandler` is a 400-line god handler

- **Where:** `AssetBlock.Application/UseCases/Payments/HandleStripeWebhook/HandleStripeWebhookCommandHandler.cs:20-423`
- **Severity:** Medium
- **Evidence:** One class verifies webhooks, completes checkout, creates orders and entitlements, composes emails, enqueues notifications and outbox rows, *and* implements `ICheckoutCompletionService`. It is simultaneously the most business-critical and least testable unit in the codebase.
- **Fix:** Split into `CheckoutCompletionOrchestrator` plus focused collaborators (`OrderFactory`, `CheckoutNotificationPublisher`), registered from `DependencyInjection.cs`. Do this after A1, so the error contract is settled first.

### A8. Catalog and review handlers hand-roll JSON caching while `ITypedCache` exists

- **Where:** `Common/Caching/JsonTypedCache.cs:7-77` versus `Assets/GetAssets/GetAssetsQueryHandler.cs:17-47`, `Tags/GetTags/GetTagsQueryHandler.cs:16-45`, `Reviews/GetReviews/GetReviewsQueryHandler.cs:17-58`, `Categories/GetCategories/GetCategoriesQueryHandler.cs:17`
- **Severity:** Medium
- **Evidence:** The analytics handlers inject `ITypedCache`; the four catalog/review handlers each hand-write get/deserialize/set/invalidate. Four copies of cache error handling means four chances to get fail-open semantics wrong — which matters given `RedisCacheService` returns `null` on outage (see [D5](#d5-redis-read-failures-are-indistinguishable-from-cache-misses)).
- **Fix:** Migrate the four handlers to `ITypedCache`. If the pattern is identical enough, a `CachedQueryBehavior` pipeline behavior removes it from handlers entirely — this is exactly the cross-cutting concern your mediator pipeline exists for.

### A9. Cache TTLs are per-handler magic numbers

- **Where:** `GetAssetsQueryHandler.cs:17`, `GetTagsQueryHandler.cs:16`, `GetReviewsQueryHandler.cs:17`, `GetCategoriesQueryHandler.cs:17`
- **Severity:** Medium
- **Evidence:** `TimeSpan.FromMinutes(2)`, `(5)`, `(10)` inline, while the analytics side correctly uses `AnalyticsConstants.*_CACHE_TTL_SECONDS`. There is no single place to see or tune catalog freshness.
- **Fix:** Add `CatalogCacheConstants` in Domain alongside `CacheKeys` and reference it from all four handlers.

### A10. Validation bounds are magic numbers while Domain constants exist

- **Where:** `UseCases/Assets/UploadAsset/UploadAssetCommandValidator.cs:17-27` versus `Domain/Core/Constants/ListingSuggestionBounds.cs:5-6`; `Validators/Tags/CreateTagCommandValidator.cs:12-13` and `Validators/Tags/UpdateTagCommandValidator.cs:13-14` versus `ListingSuggestionBounds.cs:7`
- **Severity:** Medium
- **Evidence:** Validators use `.MaximumLength(500)` and `5000` while `ListingSuggestionBounds.TITLE_MAX_LENGTH` / `DESCRIPTION_MAX_LENGTH` hold the same values. The tag slug regex and `MaximumLength(50)` are copy-pasted between create and update. These same numbers are hand-copied a third time into frontend Zod schemas (see [F14](#f14-contract-bounds-are-hand-copied-into-zod-schemas)), so one field length now lives in three places across two languages.
- **Fix:** Reference `ListingSuggestionBounds` from the asset validators; add `TagConstants.SLUG_PATTERN` and a shared `TagNameRules` for the tag pair. Then make the frontend import the same numbers (F14) so there is one source of truth.

### A11. AI error codes break the `ERR_*` convention the class documents

- **Where:** `Domain/Core/Constants/ErrorCodes.cs:4`, `:133-149`
- **Severity:** Medium
- **Evidence:** The class doc comment states all values are `ERR_`-prefixed, but the AI codes are `"AI_DISABLED"`, `"AI_ERROR"`, and similar. Frontend error parsing keys off this prefix convention.
- **Fix:** Rename to `ERR_AI_*` and update `ErrorCodesToErrorMessages`, the handlers, and the frontend parser in one change — this is a contract change, so all sources move together per `asblock-backend/AGENTS.md:36`.

### A12. Listing-copilot handlers return bare `NotFound()` with no error code

- **Where:** `Assets/EnqueueListingCopilot/EnqueueListingCopilotCommandHandler.cs:27`, `Assets/GetListingCopilotSuggestion/GetListingCopilotSuggestionQueryHandler.cs:19`, `:26`
- **Severity:** Medium
- **Evidence:** `return Result.NotFound();` with no payload, unlike the neighbouring `GetSellerAssetDetailQueryHandler.cs:19` which passes an `ErrorCodes` value. The frontend gets a 404 it cannot distinguish or message.
- **Fix:** Return `Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND)`, or add `ERR_LISTING_COPILOT_NOT_FOUND` with an `ErrorCodesToErrorMessages` entry.

### A13. Collection mutation handlers repeat the same ownership-and-state preamble

- **Where:** `Collections/AddCollectionItem/AddCollectionItemCommandHandler.cs:26-68`, `Collections/RemoveCollectionItem/RemoveCollectionItemCommandHandler.cs:26-50`, `Collections/ReorderCollectionItems/ReorderCollectionItemsCommandHandler.cs:26-62`, `Collections/UpdateCollection/UpdateCollectionCommandHandler.cs:36`, `Collections/PublishCollection/PublishCollectionCommandHandler.cs`
- **Severity:** Medium
- **Evidence:** Five handlers repeat `GetForUpdate` → seller check → archived/status guard → `GetSellerDetail`, with the same `outcome` variable plumbing. An authorization check duplicated five times is an authorization check that will eventually be five different checks.
- **Fix:** Add a `CollectionMutationGuard` returning `Result`, or an `ICollectionStore.GetMutableSellerCollection` store method that encodes the preconditions once.

### A14. Bundle archive and restore handlers are near-identical

- **Where:** `Bundles/ArchiveBundle/ArchiveBundleCommandHandler.cs:18-62`, `Bundles/RestoreBundle/RestoreBundleCommandHandler.cs:18-62`
- **Severity:** Low
- **Evidence:** Same load-by-id → seller check → state guard → `TryArchive`/`TryRestore` → audit sequence, differing only in which entity method is called.
- **Fix:** Extract a shared `BundleLifecycleTransition` helper parameterized by the transition, keeping the two command types for the API surface.

### A15. Tag and description normalization duplicated across two list handlers

- **Where:** `Assets/GetAssets/GetAssetsQueryHandler.cs:51-73`, `Users/GetMyListings/GetMyListingsQueryHandler.cs:21-43`
- **Severity:** Low
- **Evidence:** `NormalizeTags` and `NormalizeDescriptions` are byte-identical private methods in both handlers.
- **Fix:** Create `AssetListRequestNormalizer` in `Application/Common` and call it from both.

### A16. `AddAssetTag` loads the asset's tag graph to check for a duplicate

- **Where:** `Assets/AddAssetTag/AddAssetTagCommandHandler.cs:22`, `:51`
- **Severity:** Medium
- **Evidence:** `GetById` then `asset.AssetTags.Any(...)`, which requires the whole tag navigation to be materialized just to answer "does this pair exist". Note `AssetStore` already has a `HasAssetTag` method (`AssetStore.cs:517`) that does this as an `EXISTS`.
- **Fix:** Call the existing `HasAssetTag` instead of loading the graph, and add an `IAssetStore.TryAddTag` for the insert.

### A17. `GetAssetVersions` can issue the same query twice

- **Where:** `Assets/GetAssetVersions/GetAssetVersionsQueryHandler.cs:17-30`
- **Severity:** Medium
- **Evidence:** Calls `ListVersions(includeDeletedAsset: false)`, and if the result is empty and a requester is present, calls it again with `true`. Every legitimate empty result costs two round-trips, and the two-call structure encodes authorization logic in the handler rather than the query.
- **Fix:** Push the visibility rule into `IAssetStore.ListVersions` as a single query with an authorization predicate.

### A18. `GetCheckoutStatus` uses private string literals for a public contract

- **Where:** `Payments/GetCheckoutStatus/GetCheckoutStatusQueryHandler.cs:14-16`, `:31-57`
- **Severity:** Medium
- **Evidence:** `STATUS_PENDING = "pending"` and friends are `private const` in the handler, but they are returned to the browser and switched on by the frontend (`components/reviews/post-checkout-review-banner.tsx`). A public API enum is defined in a private field.
- **Fix:** Add a `CheckoutStatus` enum or `CheckoutStatusConstants` in Domain, expose it through OpenAPI, and have the frontend derive its union type from the same contract.

### A19. Category-in-use conflict returns a generic bad-request code

- **Where:** `Categories/DeleteCategory/DeleteCategoryCommandHandler.cs:39-42`
- **Severity:** Low
- **Evidence:** `CategoryInUseException` maps to `ErrorCodes.ERR_BAD_REQUEST`, so the admin UI cannot say "this category still has assets" — it can only show a generic failure.
- **Fix:** Add `ERR_CATEGORY_IN_USE` to `ErrorCodes` + `ErrorCodesToErrorMessages` and return `Result.Conflict(...)`.

### A20. Unexpected-failure handling is inconsistent across handlers

- **Where:** `Reviews/DeleteReview/DeleteReviewCommandHandler.cs:57-61` versus `Collections/AddCollectionItem/AddCollectionItemCommandHandler.cs:91-98`; helper at `Common/ResultError.cs:14-18`
- **Severity:** Medium
- **Evidence:** `DeleteReview` swallows exceptions into `ERR_INTERNAL`; the collection handlers log and rethrow. `asblock-backend/AGENTS.md:66` says to catch only to recover, translate, retry, or add context — the swallow path violates that, and the inconsistency means an operator cannot predict whether a failure will appear in logs as an exception.
- **Fix:** Pick one policy — rethrow unexpected exceptions and let the global handler translate them (which also fixes [C6](#c6-the-global-exception-handler-returns-500-without-logging-the-exception)) — and apply it everywhere. Reserve `ERR_INTERNAL` for genuinely translated infrastructure failures.

### A21. Validators live under two competing folder conventions

- **Where:** `AssetBlock.Application/Validators/Assets/UpdateAssetCommandValidator.cs:1` and `Validators/Tags/CreateTagCommandValidator.cs:1` versus `UseCases/Assets/UploadAsset/UploadAssetCommandValidator.cs:1`
- **Severity:** Low
- **Evidence:** Some validators sit in a top-level `Validators/` tree, others are co-located with their use case. `asblock-backend/AGENTS.md:30` specifies co-location. An agent told to "follow neighbouring structure" will pick whichever it finds first.
- **Fix:** Move `Validators/Assets` and `Validators/Tags` into their use-case folders and delete the `Validators/` tree. This also directly supports [H2](#h2-guides-describe-patterns-in-prose-but-never-point-at-a-canonical-implementation).

### A22. New listing-copilot validators are `public` where peers are `internal`

- **Where:** `Assets/EnqueueListingCopilot/EnqueueListingCopilotCommandValidator.cs:5`, `Assets/GetListingCopilotSuggestion/GetListingCopilotSuggestionQueryValidator.cs:5`
- **Severity:** Nit
- **Evidence:** `public sealed class` against the `internal sealed` convention used elsewhere. These are among the newest files in the repo, which is a small sign of the drift H2 describes.
- **Fix:** Change to `internal sealed`.

### A23. Suggestion content hash is sensitive to tag ordering

- **Where:** `Domain/Core/ListingSuggestionCanonicalizer.cs:19-29`
- **Severity:** Medium
- **Evidence:** `CanonicalSuggestion` serializes `suggestion.Tags` in the order received, so two AI responses with the same tags in a different order produce different hashes. For a canonicalizer whose purpose is deduplication and idempotency, order-sensitivity defeats the point — and LLM output ordering is not stable.
- **Fix:** Sort tags (ordinal) inside `ComputeContentHash` before serialization, and add an order-invariance case to `ListingSuggestionCanonicalizerTests`.

### A24. Money is a bare `decimal`, with a separate cents pipeline for analytics

- **Where:** `Domain/Core/Entities/Asset.cs:11`, `UseCases/SellerAnalytics/GetSellerAnalyticsOverview/GetSellerAnalyticsOverviewQueryHandler.cs:65-70`, `AnalyticsRange.ToCents`, `BundlePriceAllocator`
- **Severity:** Low
- **Evidence:** Assets store `decimal Price`; analytics converts to integer cents; bundles allocate prices with their own rounding rules. Three representations of money with three rounding behaviours, in a project whose roadmap includes a double-entry ledger (`assetblock.md:74`).
- **Fix:** Introduce a `UsdAmount` value object in Domain wrapping integer cents, with explicit rounding, and migrate assets/bundles/analytics onto it. Worth doing *before* the finance work in P4, not after.

### A25. Entities are anemic; invariants live in handlers

- **Where:** `Domain/Core/Entities/Asset.cs:5-25`, `Domain/Core/Entities/Review.cs:5-14` versus `UseCases/Reviews/CreateReview/CreateReviewCommandHandler.cs:35-59`
- **Severity:** Low
- **Evidence:** Entities are property bags. Review eligibility, the purchase time window, and the self-review rule exist only in `CreateReviewCommandHandler`, so nothing prevents a second code path from creating an invalid `Review`. Note `asblock-backend/AGENTS.md:7` deliberately keeps Domain as a data/abstraction layer, so this is a genuine design choice rather than an oversight — the finding is about *invariants* specifically, not about relocating logic wholesale.
- **Fix:** Add narrow factory methods for the rules that must never be bypassed — `Review.CreateForPurchase(...)` encapsulating window and self-review checks is the highest-value one. Leave the rest of the model as-is.

### A26. Missing handler tests on three untested use cases

- **Where:** no test files for `Users/GetMyListings/GetMyListingsQueryHandler.cs:8-43`, `Assets/GetAssetVersions/GetAssetVersionsQueryHandler.cs:16-47`, `Payments/GetCheckoutStatus/GetCheckoutStatusQueryHandler.cs:18-57`
- **Severity:** Medium
- **Evidence:** All three contain real branching logic with zero coverage: tag normalization, the soft-delete visibility fallback from A17, and the pending/completed/cancelled status mapping plus an ownership check at `:23-25`. The checkout-status one gates a payment-facing UI.
- **Fix:** Add `GetCheckoutStatusQueryHandlerTests` first (each status plus wrong-user not-found), then `GetAssetVersionsQueryHandlerTests` (public not-found, author sees deleted, purchaser visibility), then `GetMyListingsQueryHandlerTests`.

### A27. Analytics overview handler mixes fetching with 100 lines of DTO math

- **Where:** `UseCases/SellerAnalytics/GetSellerAnalyticsOverview/GetSellerAnalyticsOverviewQueryHandler.cs:42-147`; tests at `AssetBlock.Application.Tests/UseCases/SellerAnalytics/GetSellerAnalyticsOverviewQueryHandlerTests.cs:31-97`
- **Severity:** Medium
- **Evidence:** One store call followed by ~100 lines of inline metric arithmetic and mapping, while sibling features have `AnalyticsProductMapper` and `AnalyticsEngagementMapper`. The tests consequently build large mocked DTOs that restate the mapping rather than asserting the formulas.
- **Fix:** Extract `SellerAnalyticsOverviewMapper` following the existing mapper pattern, move the arithmetic assertions into `SellerAnalyticsOverviewMapperTests`, and reduce the handler tests to cache-hit/miss plus store invocation.

### A28. Hardcoded initial release notes

- **Where:** `UseCases/Assets/UploadAsset/UploadAssetCommandHandler.cs:129`
- **Severity:** Nit
- **Evidence:** `ReleaseNotes = "Initial release"` — a user-visible string literal in a handler.
- **Fix:** Add `AssetVersionDefaults.INITIAL_RELEASE_NOTES` to Domain constants.

---

## B. Backend — Persistence, SQL & LINQ

Three findings in this section I verified by reading the code directly; they are marked **(verified)**.

### B1. Review average is computed in memory over every rating row

- **Where:** `AssetBlock.Infrastructure/Persistence/Stores/ReviewStore.cs:15-25` **(verified)**
- **Severity:** High
- **Evidence:** `.Select(r => r.Rating).ToListAsync(...)` then `ratings.Average(...)`. Every asset-detail view transfers one row per review to compute a single number. A popular asset with 5,000 reviews moves 5,000 rows to compute one double.
- **Fix:** `return await query.AverageAsync(r => (double?)r.Rating) ?? 0d;` — one line, and PostgreSQL does the work.

### B2. Mark-all-read loads every unread notification and updates them one by one

- **Where:** `AssetBlock.Infrastructure/Persistence/Stores/NotificationStore.cs:127-153` **(verified)**
- **Severity:** High
- **Evidence:** `.Where(...ReadAt == null).ToListAsync()`, then a `foreach` setting `row.ReadAt`, then `SaveChangesAsync`. A user with a large unread backlog materializes and tracks all of it to perform what is semantically one `UPDATE`.
- **Fix:** Replace the whole method body with a single `ExecuteUpdateAsync(setters => setters.SetProperty(n => n.ReadAt, now))` on the same predicate, returning the affected row count.

### B3. Purchases library filters and sorts on columns with no matching index

- **Where:** `Persistence/Stores/PurchaseStore.cs:89-110`, `Migrations/ApplicationDbContextModelSnapshot.cs:1535-1543`
- **Severity:** High
- **Evidence:** `ListForUser` filters `UserId` and orders by `PurchasedAt`, but the only index is the unique `UIX_purchases_user_asset` on `(UserId, AssetId)` — useful for the entitlement lookup, useless for this sort. Every library page load sorts in memory after an index scan.
- **Fix:** Add a composite index `(UserId, PurchasedAt DESC, Id)` in `PurchaseConfiguration`. The trailing `Id` also gives the deterministic tie-breaker B12 wants.

### B4. Catalog and seller-listing pages run a correlated AVG subquery per row

- **Where:** `Persistence/Stores/AssetStore.cs:299`, `:377`
- **Severity:** High
- **Evidence:** `a.Reviews.Average(r => (double?)r.Rating)` inside the page projection, so PostgreSQL executes one aggregate subquery per returned asset. At page size 24 that is 24 aggregate scans per catalog page, and it grows with review volume rather than page size.
- **Fix:** Maintain a denormalized `RatingAverage`/`RatingCount` on `assets`, updated when a review is created, moderated, or deleted. A materialized view is the alternative but adds refresh complexity; given reviews are low-write, denormalized columns are the better trade here.

### B5. Seller listing projection repeats the same version subquery six times

- **Where:** `Persistence/Stores/AssetStore.cs:300-309`, `:337-346`
- **Severity:** High
- **Evidence:** Six or more separate `Versions.OrderByDescending(...).Select(...).FirstOrDefault()` expressions in one projection, each becoming its own correlated subquery over `asset_versions` — all to read different columns of the *same* latest version row.
- **Fix:** Collapse into one subquery: `Versions.OrderByDescending(v => v.VersionNumber).Select(v => new { v.Id, v.VersionNumber, ... }).FirstOrDefault()`, then read fields off that anonymous object.

### B6. Asset detail loads three navigations without split query

- **Where:** `Persistence/Stores/AssetStore.cs:81-86`
- **Severity:** Medium
- **Evidence:** `Include(Category)`, `Include(Author)`, and `Include(AssetTags → Tag)` in one query without `AsSplitQuery()`. The collection include multiplies the wide `assets` row (description, metadata) by the tag count.
- **Fix:** Project into a DTO with an explicit `Select` — preferable here since this is a read path and `asblock-backend/AGENTS.md:42` asks for projections. `AsSplitQuery()` is the smaller change if you want the entity graph.

### B7. Bundle asset locking issues one `SELECT … FOR UPDATE` per asset

- **Where:** `Persistence/Stores/BundleStore.cs:488-494`
- **Severity:** High
- **Evidence:** A `foreach (assetId)` loop, each iteration running its own `SELECT * FROM assets … FOR UPDATE`. This is N round-trips inside a transaction holding locks, and because lock order follows list order rather than a canonical order, two concurrent bundle operations on overlapping assets can deadlock.
- **Fix:** One statement: `WHERE "Id" = ANY(@ids) ORDER BY "Id" FOR UPDATE`. The `ORDER BY "Id"` is the part that prevents the deadlock, so keep it even if you retain the loop for other reasons.

### B8. Collection item insert computes position without locking the collection

- **Where:** `Persistence/Stores/CollectionStore.cs:317-355`
- **Severity:** High
- **Evidence:** `MaxAsync(Position) + 1` followed by an insert, with no `FOR UPDATE` on the parent collection row. Two concurrent adds read the same max and the second violates `UIX_collection_items_collection_position`. `asblock-backend/AGENTS.md:46` specifically forbids relying on a prior read when a race can produce duplicate effects.
- **Fix:** Lock the collection row with the existing `GetForUpdate` before computing the position, or do the insert as a single SQL statement using a scalar subquery for `max+1` and retry on conflict.

### B9. Analytics rollup holds a `RepeatableRead` transaction across eight table scans

- **Where:** `Persistence/Stores/AnalyticsEventStore.cs:48-110`
- **Severity:** High
- **Evidence:** A `RepeatableRead` transaction wraps eight or more `ExecuteSqlRawAsync` rollup statements over `analytics_events` before committing. Long high-isolation transactions block vacuum on the highest-churn table in the system and hold snapshots open for the full duration.
- **Fix:** Drop to `READ COMMITTED` and serialize the worker with a PostgreSQL advisory lock instead of relying on isolation level, committing per rollup table. Aggregation is idempotent, so per-table commits are safe.

### B10. Seller analytics overview awaits ten sequential round-trips

- **Where:** `Persistence/Stores/SellerAnalyticsStore.cs:51-113`
- **Severity:** High
- **Evidence:** `GetOverviewSnapshot` awaits roughly ten separate SQL batches one after another inside a read-only transaction. Latency is the sum of all ten, so the seller dashboard pays ten network round-trips before rendering.
- **Fix:** Combine into one multi-statement query with CTEs returning a single row of metrics. Since it is read-only, issuing the independent queries concurrently on separate connections is the cheaper interim fix.

### B11. Archive analysis reads always fetch the large text and JSON columns

- **Where:** `Persistence/Stores/AssetArchiveAnalysisStore.cs:11-13`
- **Severity:** Medium
- **Evidence:** `FirstOrDefaultAsync` on the full `AssetArchiveAnalysis` entity, which holds up to 16 KB of readme plus manifest JSON (`appsettings.json:178-180`), even for callers that only need status metadata.
- **Fix:** Add a metadata-only projection method and reserve the full read for the listing-copilot path that actually needs the content.

### B12. Pagination lacks a deterministic tie-breaker in four stores

- **Where:** `Persistence/Stores/ReviewStore.cs:117-121`, `Persistence/Stores/NotificationStore.cs:57-68`, `Persistence/Stores/CategoryStore.cs:43-48`, plus `PurchaseStore.cs:89-110` (see B3)
- **Severity:** Medium
- **Evidence:** Sorts on `Rating`, `CreatedAt`, `ReadAt`, `Name`, and `Slug` with no `ThenBy(x => x.Id)`. PostgreSQL gives no ordering guarantee for equal keys, so rows can repeat or vanish across pages — most visible on reviews sorted by rating, where ties are the norm. `asblock-backend/AGENTS.md:43` requires deterministic ordering for paged results. `NotificationStore` additionally has no `MAX_PAGE_SIZE` clamp, which is what makes [A6](#a6-commands-and-queries-missing-validators-entirely) exploitable.
- **Fix:** Add `.ThenBy(x => x.Id)` to every sort branch in all four stores, and add the `Math.Clamp` page-size guard to `NotificationStore` to match its siblings.

### B13. No global soft-delete query filter; `GetById` returns deleted assets

- **Where:** `Persistence/ApplicationDbContext.cs:42-63`, `Persistence/Stores/AssetStore.cs:86`
- **Severity:** Medium
- **Evidence:** No `HasQueryFilter(a => a.DeletedAt == null)` on `Asset`, so every read path must remember its own `DeletedAt == null` predicate — and `GetById` at `:86` does not, returning soft-deleted assets to callers that then have to filter. Given that soft delete is a *business invariant* protecting purchaser access (`asblock-backend/AGENTS.md:23`), leaving it to per-query discipline is the wrong default.
- **Fix:** Add a global query filter on `Asset` and use `IgnoreQueryFilters()` explicitly in the few paths that legitimately need deleted rows (author access, orphan cleanup, download for existing purchasers). Audit those call sites as part of the change.

### B14. Text search uses `ToLower().Contains`, which cannot use an index

- **Where:** `Persistence/Stores/BundleStore.cs:202-206`, `:236-240`; `Persistence/Stores/CollectionStore.cs:157-160`, `:222-225`; `Persistence/Stores/CategoryStore.cs:29-33`
- **Severity:** Medium
- **Evidence:** `r.Title.ToLower().Contains(term)` translates to `lower(...) LIKE '%term%'`, which no btree index can serve, producing a sequential scan on every bundle, collection, and category search. The main asset catalog does this correctly with `tsvector` + `pg_trgm`; these three secondary searches were not given the same treatment.
- **Fix:** Switch to `EF.Functions.ILike` and add GIN `gin_trgm_ops` indexes on the searched columns (`bundle_revisions` title/description, `collections.Title`, `categories.Name`/`Slug`).

### B15. Catalog search ORs three strategies into one predicate

- **Where:** `Persistence/Stores/AssetStore.cs:392-398`
- **Severity:** Medium
- **Evidence:** `tsvector @@ query OR ILike OR similarity()` in a single `WHERE`. An `OR` across a GIN full-text index, a trigram index, and a plain pattern match generally defeats index-only plans; the planner falls back to scanning and applying all three. Results are also unranked, so relevance is arbitrary.
- **Fix:** Restructure as a ranked union — full-text with `ts_rank` first, trigram similarity as a second branch, each with its own `LIMIT` — then merge and order by rank. This also gives you the relevance ordering the roadmap's search evaluation work (`assetblock.md:62`) will need.

### B16. `AvailablePublicBundles` runs nested `All`/`Any` per row

- **Where:** `Persistence/Stores/BundleStore.cs:505-517`
- **Severity:** Medium
- **Evidence:** `r.Items.All(i => i.Asset… && i.Asset.Versions.Any(v => v.IsCurrent))` evaluated for every bundle in a list page — a nested correlated existence check three levels deep, on the public browse path.
- **Fix:** Rewrite as SQL `NOT EXISTS (… invalid item …)`, which the planner handles far better, or maintain an `IsAvailable` flag on the revision updated when items change.

### B17. Public bundle detail scans availability twice

- **Where:** `Persistence/Stores/BundleStore.cs:30-35`
- **Severity:** Medium
- **Evidence:** `AvailablePublicBundles().AnyAsync(b => b.Id == id)` for the visibility check, then `LoadDetail` re-walks revisions and items — so the expensive predicate from B16 runs twice per detail view.
- **Fix:** Embed the availability predicate in the detail query and return null when it does not match.

### B18. Outbox claim reads full payload rows twice

- **Where:** `Persistence/Stores/OutboxStore.cs:44-79`
- **Severity:** Medium
- **Evidence:** `FromSqlInterpolated` with `SELECT *` to claim, then a second query reloads the claimed messages with their payloads. Outbox payloads are JSON blobs, so both round-trips carry them.
- **Fix:** Claim returning `Id` only, then load payloads once for the claimed set.

### B19. Row locks use `SELECT *` on wide tables

- **Where:** `Persistence/Stores/AssetStore.cs:94-97`, `Persistence/Stores/BundleStore.cs:21-24`, `Persistence/Stores/CollectionStore.cs:26-29`, `Persistence/Stores/CheckoutIntentStore.cs:199-211`
- **Severity:** Medium
- **Evidence:** All four `FOR UPDATE` queries select every column, including descriptions and metadata, when the purpose is only to take a lock. In the checkout-cleanup case (`:199-211`) only `Id` is subsequently used.
- **Fix:** Use key-only lock queries (`SELECT "Id" … FOR UPDATE`), and for the cleanup path add `SKIP LOCKED` and follow with `ExecuteUpdate` by id.

### B20. Unread notifications have no supporting partial index

- **Where:** `Persistence/Configurations/UserNotificationConfiguration.cs:28`, `Persistence/Stores/NotificationStore.cs:44-49`
- **Severity:** Medium
- **Evidence:** The hot query filters `RecipientUserId` + `ReadAt == null` and sorts by `CreatedAt`, but the index is `(RecipientUserId, CreatedAt)` with no `ReadAt` predicate. The unread badge polls this on every page.
- **Fix:** Add a partial index: `(RecipientUserId, CreatedAt DESC, Id) WHERE "ReadAt" IS NULL`. Partial indexes stay small because read notifications drop out of them.

### B21. Refresh tokens accumulate forever

- **Where:** `AssetBlock.Infrastructure/Services/JwtTokenService.cs:57-69` **(verified)**, `Migrations/ApplicationDbContextModelSnapshot.cs:1573-1576`
- **Severity:** Medium
- **Evidence:** Every login inserts a new `refresh_tokens` row (`:68`) and nothing ever deletes expired or revoked ones. With a 7-day TTL and no cleanup, the table grows monotonically with total logins forever, and `ValidateRefreshToken` scans a permanently growing index.
- **Fix:** Add a retention worker deleting `WHERE "ExpiresAt" < now()` (mirroring the outbox retention worker from [D9](#d9-processed-outbox-rows-are-never-purged)), plus a partial index on active tokens. This also pairs with the logout work in [C2](#c2-there-is-no-logout-endpoint-so-logging-out-does-not-invalidate-the-refresh-token).

### B22. Several read paths issue avoidable extra existence queries

- **Where:** `Persistence/Stores/AssetStore.cs:145-167` (four queries), `Persistence/Stores/CheckoutIntentStore.cs:105-124` (two `AnyAsync`), `Persistence/Stores/ListingCopilotStore.cs:190-218` (ownership + data), `Persistence/Stores/BundleStore.cs:402-452` (three queries)
- **Severity:** Medium
- **Evidence:** A recurring shape: one or more `AnyAsync` authorization/existence probes followed by the real query. `ListVersions` can reach four round-trips; `HasActiveForAsset` runs two separate `EXISTS` for what is one question; the bundle checkout snapshot takes three.
- **Fix:** Fold the authorization predicate into the data query and return flags or null, rather than probing first. For `HasActiveForAsset`, one SQL statement with `EXISTS (…) OR EXISTS (…)`.

### B23. Job enqueue reads before inserting

- **Where:** `Persistence/Stores/AssetProcessingJobStore.cs:73-81`
- **Severity:** Medium
- **Evidence:** `FirstOrDefaultAsync` on the id before inserting, with the unique-violation catch as the actual correctness mechanism. The read is therefore pure overhead — it cannot close the race, only make it less likely.
- **Fix:** `INSERT … ON CONFLICT DO NOTHING RETURNING "Id"` in one statement. The unique index remains the invariant; the read disappears.

### B24. Revoking a single refresh token uses a tracked load

- **Where:** `AssetBlock.Infrastructure/Services/JwtTokenService.cs:92-103` **(verified)**
- **Severity:** Low
- **Evidence:** `FirstOrDefaultAsync` then mutate then `SaveChangesAsync`, while the sibling `RevokeAllRefreshTokens` at `:105-112` correctly uses `ExecuteUpdateAsync`. Two adjacent methods, two patterns.
- **Fix:** `ExecuteUpdateAsync` on `Id == tokenId && RevokedAt == null`, matching the method below it.

### B25. `HasAssetTag` runs as a tracking query

- **Where:** `Persistence/Stores/AssetStore.cs:517-519`
- **Severity:** Low
- **Evidence:** `dbContext.Set<AssetTag>().AnyAsync(...)` without `AsNoTracking()`. Harmless in effect but inconsistent with the surrounding read methods.
- **Fix:** Add `.AsNoTracking()`.

### B26. Paged review list materializes full entity graphs

- **Where:** `Persistence/Stores/ReviewStore.cs:91-128`
- **Severity:** Low
- **Evidence:** The paged endpoint returns `Review` plus `User` entities rather than projecting the fields the API exposes, so every review page loads user rows in full.
- **Fix:** `Select` into a `ReviewListItem` DTO with only the API fields.

### B27. Social platform lookup by id scans the cached list

- **Where:** `Persistence/Stores/SocialPlatformStore.cs:31-35`
- **Severity:** Low
- **Evidence:** `GetById` calls `GetAll` then `FirstOrDefault` in memory. Harmless today because the table is tiny, but it is a lookup-by-primary-key implemented as a full scan.
- **Fix:** `FirstOrDefaultAsync(p => p.Id == id)`, with the cache keyed by id.

### B28. Download authorization uses a cross join that can duplicate rows

- **Where:** `Persistence/Stores/AssetStore.cs:575-589`
- **Severity:** Low
- **Evidence:** Multiple `from` clauses over purchases and versions with no `Distinct`, so the SQL join can return duplicate purchase rows. It currently feeds a boolean decision so the duplicates are harmless, but the shape is fragile for a code path that gates paid content access.
- **Fix:** Express it as an `AnyAsync`/`EXISTS` predicate, which is both correct by construction and cheaper.

---



## C. Backend — WebApi & security

I dropped one finding the security lane raised — "login should revoke all prior refresh tokens" — because revoking every session on each login would break multi-device use, which is normal and desirable behaviour rather than a defect. The real gaps in that area are C1–C3.

### C1. SignalR authentication puts the access JWT in a query string and hands it to page JavaScript

- **Where:** `AssetBlock.WebApi/Extensions/JwtAuthenticationExtensions.cs:41-49` **(verified)**, `asblock-frontend/app/api/auth/signalr-access/route.ts:12-33` **(verified)**
- **Severity:** High
- **Evidence:** The backend reads `ctx.Request.Query["access_token"]` and assigns it to `ctx.Token` for the notifications hub. The frontend exposes a `GET` returning `{ accessToken }` to page JS so `@microsoft/signalr` can supply it. Both files carry accurate comments acknowledging the trade-off, so this is a known decision — but it means the whole BFF/httpOnly-cookie design (`asblock-frontend/AGENTS.md:33-34`) has one deliberate hole, and any XSS anywhere upgrades directly to full session theft. Query-string tokens also land in proxy and CDN access logs by default.
- **Fix:** Have the backend mint a short-lived (60–120 s), hub-scoped token with its own audience that grants nothing but hub connection, and return that instead of the session JWT. This satisfies the WebSocket constraint while making a leaked value nearly worthless. The `Cache-Control: no-store` at `route.ts:29` is correct and should stay.

### C2. There is no logout endpoint, so logging out does not invalidate the refresh token

- **Where:** `AssetBlock.WebApi/Controllers/AuthController.cs:23-126` **(verified)**, `AssetBlock.Infrastructure/Services/JwtTokenService.cs:92-112` **(verified)**, `asblock-frontend/app/api/auth/logout/route.ts:10-12`
- **Severity:** High
- **Evidence:** `AuthController` exposes login, refresh, register, password-reset request/confirm, email-verification-confirm, and email-change-confirm — and nothing else. There is no logout route. `RevokeRefreshToken` and `RevokeAllRefreshTokens` both exist and work, but are reachable only from password-reset and email-change handlers. The frontend logout therefore just deletes cookies locally, leaving the refresh token valid server-side for its full 7-day TTL: a token captured before logout still works after it.
- **Fix:** Add `POST /api/auth/logout` that takes the current refresh token and calls the existing `RevokeRefreshToken`, then have `app/api/auth/logout/route.ts` call it before `clearAuthCookies`. The service layer is already built — this is wiring, and it is the highest-value security fix in the repo.

### C3. No refresh-token reuse detection

- **Where:** `AssetBlock.Application/UseCases/Auth/RefreshToken/RefreshTokenCommandHandler.cs:21`, `AssetBlock.Infrastructure/Services/JwtTokenService.cs:73-90` **(verified)**
- **Severity:** Medium
- **Evidence:** `ValidateRefreshToken` filters on `RevokedAt == null && ExpiresAt > now` and returns `null` otherwise, so a presented *revoked* token is indistinguishable from a garbage one — both map to `ERR_AUTH_TOKEN_INVALID`. Presenting an already-rotated token is the classic signal of token theft, and it is currently ignored.
- **Fix:** When the hash matches a row that is revoked but not expired, treat it as theft: call `RevokeAllRefreshTokens` for that user and write an audit entry. Standard rotation-with-reuse-detection, and cheap since both pieces exist.

### C4. Stripe webhook buffers the entire request body with no size limit

- **Where:** `AssetBlock.WebApi/Controllers/PaymentsController.cs:116-126`
- **Severity:** High
- **Evidence:** `EnableBuffering()` then `ReadToEndAsync()` into a string, on an `[AllowAnonymous]` endpoint with no `[RequestSizeLimit]` and no rate limit. Contrast `AuthController`, which correctly applies `[RequestSizeLimit(16_384)]` to its confirm actions (`:88`, `:108`, `:126`). An unauthenticated caller can force large allocations before signature verification runs.
- **Fix:** Add `[RequestSizeLimit(262_144)]` plus a high-threshold per-IP fixed window. The size guard must come before verification, not after.

### C5. Avatar URL accepts any scheme, unlike social links

- **Where:** `AssetBlock.Application/UseCases/Users/UpdateProfile/UpdateUserProfileCommandValidator.cs:18`, `UpdateUserProfileCommandHandler.cs:35`
- **Severity:** High
- **Evidence:** The validator checks only `MaximumLength(500)` — no scheme restriction, so `javascript:`, `data:`, and `file:` values are accepted and persisted. The social-link validator in the same feature does enforce absolute `http`/`https`, so the codebase already holds the correct position two fields away. Whether it becomes stored XSS depends on rendering, but the API should not accept it either way.
- **Fix:** Apply the existing social-link URL rule to `AvatarUrl`, ideally HTTPS-only since avatars are always remote images.

### C6. The global exception handler returns 500 without logging the exception

- **Where:** `AssetBlock.WebApi/Extensions/ExceptionHandlerExtensions.cs:29-33`
- **Severity:** High
- **Evidence:** Non-validation exceptions are translated to `ERR_INTERNAL` and written to the response with no `ILogger` call anywhere in the handler. Every unexpected server error is invisible — no stack trace, no message, no correlation. This is the finding that most undermines your ability to operate the system, and it compounds [A20](#a20-unexpected-failure-handling-is-inconsistent-across-handlers), where some handlers also swallow exceptions into `ERR_INTERNAL` before reaching here.
- **Fix:** Inject `ILogger` and `LogError(exception, ...)` with the trace identifier before returning the problem response. Small change, disproportionate value.

### C7. `AllowedHosts` is `*` in the base configuration

- **Where:** `AssetBlock.WebApi/appsettings.json:182` **(verified)**
- **Severity:** Medium (deployment-gated)
- **Evidence:** Host filtering is disabled. `assetblock.md:86` defers host filtering to deployment, so the deferral is deliberate — I include it because the insecure value sits in the *base* `appsettings.json` rather than a Development override, making it the inherited default.
- **Fix:** Move `"AllowedHosts": "*"` into `appsettings.Development.json` and leave it unset in the base file, so a production deploy that forgets to configure hosts fails loudly instead of accepting anything.

### C8. Listing-copilot read path does not require a verified email, unlike its write path

- **Where:** `AssetBlock.WebApi/Controllers/UsersController.cs:393` (enqueue) versus `:415` (read)
- **Severity:** Medium
- **Evidence:** Enqueue requires `AuthorizationPolicies.VERIFIED_EMAIL`; the GET returning the suggestion requires only `[Authorize]`. Ownership is still enforced so this is not a data leak — but it is an inconsistent gate on a paired endpoint, and inconsistent gates are how real bypasses appear later.
- **Fix:** Apply `VERIFIED_EMAIL` to both, or move the requirement into the query handler so the two cannot diverge.

### C9. Two seller controllers reimplement `ApiControllerBase` instead of inheriting it

- **Where:** `AssetBlock.WebApi/Controllers/SellerBundlesController.cs:171`, `AssetBlock.WebApi/Controllers/SellerCollectionsController.cs:274`
- **Severity:** Medium
- **Evidence:** Both define their own `GetUserId`, `UnauthorizedProblem`, and `MapResultToActionResult` rather than inheriting. Claim parsing now exists in three places and is free to drift — which it already has, per C10.
- **Fix:** Inherit `ApiControllerBase` in both and delete the local copies.

### C10. `ApiControllerBase.GetUserId` misses the `sub` fallback that authorization uses

- **Where:** `AssetBlock.WebApi/Controllers/ApiControllerBase.cs:17-21` **(verified)** versus `AssetBlock.WebApi/Authorization/VerifiedEmailAuthorizationHandler.cs:20`
- **Severity:** Medium
- **Evidence:** The base helper reads only `ClaimTypes.NameIdentifier`; the authorization handler falls back to `JwtClaimTypes.SUB`. Since `JwtAuthenticationExtensions.cs:25` sets `MapInboundClaims = false`, claim naming is deliberately non-default — so two components disagreeing about where the subject lives is a latent bug: a token could satisfy authorization and then yield a null user id in the controller.
- **Fix:** Give `ApiControllerBase.GetUserId` the same fallback order, and add a WebApi test asserting both claim shapes resolve identically.

### C11. The `GetUserId()` null-check is copy-pasted 57 times

- **Where:** `AssetBlock.WebApi/Controllers/UsersController.cs:61-65`, `:81-85`, `:103-107` and ten more in that file alone; 57 call sites across `UsersController` (18), `SellerCollectionsController` (11), `AssetsController` (9), `SellerAnalyticsController` (7), `SellerBundlesController` (7), `PaymentsController` (3), `ReviewsController`, `AnalyticsController` **(verified)**
- **Severity:** Medium
- **Evidence:** The identical five-line block `var userId = GetUserId(); if (userId is null) { return UnauthorizedProblem(); }` appears 57 times — roughly 285 lines existing only because the helper returns `Guid?`. One site skips the check and uses `GetUserId()!.Value` (`ReviewsController.cs:28`), which throws if the claim is malformed. That is the predictable outcome of repeating a pattern 57 times.
- **Fix:** Add `protected bool TryGetUserId(out Guid userId)`, or better an `ICurrentUser` scoped service injected into handlers so the identity arrives with the request instead of being re-derived per action. Either way, fix `ReviewsController.cs:28` now.

### C12. CORS allows any header and any method with credentials

- **Where:** `AssetBlock.WebApi/Extensions/CorsExtensions.cs:42`
- **Severity:** Medium
- **Evidence:** `.AllowAnyHeader().AllowAnyMethod()` with `.AllowCredentials()`. The origin list is explicit and configured, which is the part that matters most, so this is not exploitable alone — but with credentials enabled the policy is broader than the single known frontend needs.
- **Fix:** Enumerate the methods and headers the frontend actually sends (`Authorization`, `Content-Type`, the analytics signature header).

### C13. Analytics rate limiting fails open when Redis is unavailable

- **Where:** `AssetBlock.WebApi/Extensions/RateLimitingExtensions.cs:49`, `AssetBlock.WebApi/RateLimiting/AnalyticsDistributedRateLimiterAdapter.cs:69`
- **Severity:** Medium
- **Evidence:** An `UNAVAILABLE` lease returns HTTP 202 for `ANALYTICS_EVENTS`, so a Redis outage silently removes the limit on the anonymous ingest endpoint. Deliberate degradation for telemetry is defensible — `.env.example:34` documents the same 202-on-missing-context behaviour — but "limiter is down" and "event accepted" should not be the same response.
- **Fix:** Return 503 when the distributed limiter is unavailable in Staging/Production, keep 202 only for explicitly accepted drops (DNT, missing signature), and add a metric so the outage is visible.

### C14. Auth rate limits share one bucket for all clients with no resolvable IP

- **Where:** `AssetBlock.WebApi/Extensions/RateLimitingExtensions.cs:13`, `:132`
- **Severity:** Medium
- **Evidence:** A missing `RemoteIpAddress` partitions to the literal key `"unknown"`, so every such request for login, register, and refresh shares one bucket — either shared-fate denial or, if the limit is generous, an effective bypass. Related: `assetblock.md:86` defers forwarded-headers handling, which is what makes `RemoteIpAddress` unreliable behind a proxy in the first place.
- **Fix:** Fail closed with 429 when no client IP can be determined on auth endpoints, and resolve forwarded headers before deploying behind a proxy, since IP partitioning is meaningless until then.

### C15. Change-password has no rate limit

- **Where:** `AssetBlock.WebApi/Controllers/UsersController.cs:245`, `AssetBlock.WebApi/Extensions/RateLimitingExtensions.cs:128`
- **Severity:** Medium
- **Evidence:** `POST me/password` carries `[Authorize]` but no `[EnableRateLimiting]`, and no password-change policy exists. Since it verifies the current password, it is an authenticated password oracle — useful to an attacker holding a stolen access token who wants to confirm a guess.
- **Fix:** Add a per-user sliding window (5/hour is ample) to `RateLimitingConstants.Policies` and apply it.

### C16. Registration discloses whether an email is already registered

- **Where:** `AssetBlock.Application/UseCases/Auth/Register/RegisterCommandHandler.cs:27`, `AssetBlock.WebApi/Controllers/AuthController.cs:58`
- **Severity:** Medium
- **Evidence:** A duplicate email returns 409 with `ERR_AUTH_EMAIL_ALREADY_EXISTS`, giving unauthenticated callers a reliable account-existence oracle. The password-reset flow in the same controller deliberately avoids this (`README.md:22` calls out "anti-enumeration"), so the codebase already holds the opposite position two endpoints away.
- **Fix:** Return the same 202 "check your email" envelope for both cases and mail the existing account that someone tried to register with their address. This does cost registration UX clarity — a real trade-off — but the inconsistency with password reset is worth resolving either way.

### C17. Public health endpoints enumerate the dependency topology

- **Where:** `AssetBlock.WebApi/Extensions/HealthCheckExtensions.cs:53`, `:77`
- **Severity:** Medium
- **Evidence:** `/health/live` and `/health/ready` are `.AllowAnonymous()` and return JSON naming each check — `postgresql`, `storage`, `redis`, `clamav` — with per-dependency status. That is a free infrastructure inventory plus a live signal for when a dependency is down.
- **Fix:** Keep `/health/live` public but reduce it to `{ "status": "Healthy" }`; restrict the detailed `/health/ready` to an internal network or authenticated probe.

### C18. Client-supplied filenames are persisted and echoed into `Content-Disposition`

- **Where:** `AssetBlock.Application/UseCases/Assets/UploadAsset/UploadAssetCommandHandler.cs:45`, `AssetBlock.WebApi/Controllers/AssetsController.cs:105`
- **Severity:** Medium
- **Evidence:** `Path.GetFileName` plus an extension allowlist is the only sanitization, so control characters, quotes, and non-ASCII survive into the stored name and then into the download response header. Storage keys are built server-side (correct, per `asblock-backend/AGENTS.md:54`), so this is header-injection and client-save behaviour rather than path traversal.
- **Fix:** Normalize to a conservative ASCII subset on upload and use RFC 5987 `filename*` encoding on the response — or serve a server-generated name and keep the original as display metadata only.

### C19. JWT validation does not pin the signing algorithm

- **Where:** `AssetBlock.WebApi/Extensions/JwtAuthenticationExtensions.cs:26-37` **(verified)**
- **Severity:** Low
- **Evidence:** `TokenValidationParameters` sets issuer, audience, lifetime, and signing-key validation but no `ValidAlgorithms` or explicit `RequireSignedTokens`. The practical risk is lower than it first looks: only a `SymmetricSecurityKey` is configured, so the classic RS256→HS256 confusion attack has nothing to work with. Defense in depth, not an open hole.
- **Fix:** Add `ValidAlgorithms = [SecurityAlgorithms.HmacSha256]` and `RequireSignedTokens = true` — two lines that remove the question permanently.

### C20. BCrypt work factor is left at the library default

- **Where:** `AssetBlock.Infrastructure/Services/PasswordHasher.cs:7`
- **Severity:** Medium
- **Evidence:** `BCrypt.HashPassword(password)` with no cost parameter, so the work factor is whatever the library version defaults to — undocumented, and free to change under a dependency bump. There is also no rehash-on-login path, so early hashes keep their original cost forever.
- **Fix:** Pin the work factor explicitly (12 is the current reasonable choice) and rehash on successful login when a stored hash is below the configured cost.

### C21. Encryption wire format carries no key identifier

- **Where:** `AssetBlock.Infrastructure/Services/AesGcmEncryptionService.cs:19`, `:135`
- **Severity:** Medium
- **Evidence:** Chunks are framed as length/nonce/tag/ciphertext against a single `Encryption:KeyBase64`. Nothing in the blob records which key encrypted it, so rotation requires decrypting and re-encrypting everything — and cannot be done incrementally, because there is no way to tell which objects are already migrated.
- **Fix:** Add a version/key-id byte to the chunk header now and support a keyring (current key for writes, all keys for reads). Doing this before there is meaningful stored data is dramatically cheaper than after, which is why it is worth pulling forward even though no rotation is needed today.

### C22. Runtime accepts AES-128/192 keys that the validator rejects

- **Where:** `AssetBlock.Infrastructure/Services/AesGcmEncryptionService.cs:149` versus `AssetBlock.Infrastructure/Options/EncryptionOptionsValidator.cs:27`
- **Severity:** Low
- **Evidence:** The validator requires exactly 32 bytes, but `GetKey()` also has branches accepting 16 and 24. Unreachable through configuration today, so this is dead permissive code rather than a live weakness — but it contradicts the stated AES-256 policy.
- **Fix:** Delete the 16/24-byte branches so the runtime cannot be weaker than the validator.

### C23. Stripe webhook relies on default replay tolerance and has no event-id ledger

- **Where:** `AssetBlock.Infrastructure/Services/StripePaymentService.cs:143`
- **Severity:** Medium
- **Evidence:** `EventUtility.ConstructEvent(payload, signature, webhookSecret)` with no explicit tolerance, and no persistence of processed Stripe `event.id`. Idempotency currently rests on checkout-intent state, which covers ordinary duplicate delivery but not a replay of a different event type for the same session.
- **Fix:** Pass an explicit tolerance (300 s) and persist processed `event.id` values behind a unique index, checked before processing. `asblock-backend/AGENTS.md:24` requires idempotent purchase creation; an event-id ledger is the direct way to guarantee it.

### C24. JWT authentication failures log exception text

- **Where:** `AssetBlock.WebApi/Extensions/JwtAuthenticationExtensions.cs:56`, `:71`
- **Severity:** Low
- **Evidence:** `LogWarning(ctx.Exception, ...)` and a challenge log including `AuthenticateFailure?.Message`. Token-validation exception messages can echo token-adjacent detail, and these fire on every expired token — so it is both noisy and closer to sensitive data than a warning-level log should be.
- **Fix:** Log a stable reason code (`expired`, `bad_signature`, `bad_audience`) at Debug, without the exception object.

### C25. Upload logs the client-controlled filename

- **Where:** `AssetBlock.WebApi/Controllers/AssetsController.cs:152`
- **Severity:** Low
- **Evidence:** `LogInformation("Upload started for user {UserId}, file {FileName}", userId, file.FileName)`. With C18 (filenames unsanitized) this is a log-injection vector, and filenames often carry personal information.
- **Fix:** Log the extension plus a hash of the name, or the server-generated storage key.

### C26. OpenTelemetry records full exceptions at 100% sampling

- **Where:** `AssetBlock.WebApi/Extensions/ObservabilityExtensions.cs:46`, `appsettings.json:114-122` **(verified)**
- **Severity:** Low
- **Evidence:** `RecordException = true` on ASP.NET Core and HttpClient instrumentation with `TraceSampleRatio: 1.0`. Correct for local Aspire Dashboard use (`Observability:Enabled` defaults to `false`), but these defaults would follow the app into a hosted environment where HttpClient spans cover Stripe and AI provider calls.
- **Fix:** Leave local behaviour alone; add a config comment that production must lower the sample ratio and scrub auth/query attributes. Reasonable to defer, but record the decision.

### C27. Hardcoded route literal bypasses `ApiRoutes`

- **Where:** `AssetBlock.WebApi/Controllers/SellerCollectionsController.cs:28`, `AssetBlock.WebApi/Constants/ApiRoutes.cs:68`
- **Severity:** Low
- **Evidence:** `[Route("api/seller/collections")]` as a literal, while `ApiRoutes.SellerCollections` holds the segment constants but no `BASE`. `asblock-backend/AGENTS.md:34` requires routes to live in the constants class.
- **Fix:** Add `SellerCollections.BASE` and reference it.

### C28. Admin tag creation binds the mediator command straight from the body

- **Where:** `AssetBlock.WebApi/Controllers/TagsController.cs:57`
- **Severity:** Low
- **Evidence:** `[FromBody] CreateTagCommand command` couples the HTTP contract to the application type. Harmless while the command has only `Name`, but the moment a field is added the API surface changes silently — this is the mass-assignment shape.
- **Fix:** Bind a request DTO and map it to the command in the controller, as other controllers do.

### C29. CSV export can write problem details after streaming has begun

- **Where:** `AssetBlock.WebApi/Results/SellerAnalyticsSalesCsvExportResult.cs:36`, `:52`
- **Severity:** Low
- **Evidence:** Sets `Content-Type` and `Content-Disposition`, streams rows, then on failure calls `ResultProblemDetailsMapper` — after headers and partial body are already sent. The client gets a truncated CSV with JSON appended under a 200 status.
- **Fix:** Keep all validation in the `PrepareSellerAnalyticsSalesExportQuery` step (it is already gated there) and abort the response once streaming has started rather than trying to change status.

### C30. `.env.example` ships weak local credentials without a warning banner

- **Where:** `asblock-backend/.env.example:5`, `:11`
- **Severity:** Low
- **Evidence:** `POSTGRES_PASSWORD=postgres`, `AWS_SECRET_ACCESS_KEY=dev_seaweedfs_secret`. Correct for a local compose stack and correctly not real secrets — the only risk is someone copying the file forward.
- **Fix:** Add a header comment stating these are local-only and must never be used outside `docker-compose`.

---

## D. Backend — Integrations, workers & crypto

### D1. Outbox side effects can repeat because handlers are not idempotent

- **Where:** `AssetBlock.Infrastructure/Outbox/OutboxDispatcher.cs:106-114` **(verified)**, `AssetBlock.Infrastructure/Outbox/EmailDispatchOutboxHandler.cs:61`
- **Severity:** High
- **Evidence:** The dispatcher awaits `handler.Handle(...)` then `MarkProcessed`. If the handler succeeds but the lease is lost before the mark lands, the code logs "Lost outbox lease … after successful handler" (`:109-112`) and the row is redelivered — so `EmailDispatchOutboxHandler` sends the email twice. The dispatcher explicitly *detects* this case, which confirms it is reachable; it just cannot prevent the duplicate. At-least-once is the right design for an outbox, but it only works if handlers are idempotent, and these are not.
- **Fix:** Give each handler a dedupe key. For email, record `MessageId` in a sent-log table behind a unique index and no-op on conflict. This is the missing half of the pattern.

### D2. Outbox rows are stuck forever after ten attempts, with no dead-letter state

- **Where:** `AssetBlock.Infrastructure/Persistence/Stores/OutboxStore.cs:48-49`, `AssetBlock.Infrastructure/Outbox/OutboxDispatcher.cs:54-57`, `:74-78` **(verified)**
- **Severity:** High
- **Evidence:** The claim SQL requires `AttemptCount < MAX_ATTEMPTS` (10), so after ten failures a row is never selected again — it sits there indefinitely with nothing surfacing it. Separately, an unknown message type is "handled" by `MarkFailed(..., DateTimeOffset.UtcNow.AddYears(100), ...)` at `:78`, using a year-2126 retry timestamp as an improvised dead-letter. Either way a permanently failed side effect — an unsent purchase email, an undeleted blob — is silently dropped.
- **Fix:** Add an explicit `DEAD_LETTERED` status set on both max-attempts and missing-handler (replacing the `AddYears(100)` trick), emit a metric and log at that transition, and provide a replay path.

### D3. Redis prefix invalidation uses blocking `KEYS`

- **Where:** `AssetBlock.Infrastructure/Services/RedisCacheService.cs:79`
- **Severity:** High
- **Evidence:** `foreach (var key in server.Keys(pattern: prefix + "*"))`. This sweeps the whole keyspace on every prefix invalidation — that is, after catalog writes — so cost grows with total cached keys and it degrades every consumer of that Redis instance, not just this app.
- **Fix:** `StackExchange.Redis` maps `IServer.Keys` to `SCAN` where supported, which avoids blocking but is still a full sweep. The durable fix is a Redis set per invalidation prefix whose members are deleted explicitly, making invalidation O(affected keys).

### D4. Orphan cleanup materializes the entire object listing

- **Where:** `AssetBlock.Infrastructure/Services/S3CompatibleObjectStore.cs:96-125`, `AssetBlock.Infrastructure/HostedServices/StorageOrphanCleanupWorker.cs:81-84`
- **Severity:** High
- **Evidence:** `ListObjects` accumulates every object under `assets/` into a `List<StorageObjectInfo>` and the worker iterates the whole thing. Memory grows linearly with total stored assets — a slow-motion OOM that only appears once the marketplace holds real content.
- **Fix:** Return `IAsyncEnumerable<StorageObjectInfo>` driven by S3 continuation tokens and process in bounded batches. Cheaper to fix before the data grows.

### D5. Redis read failures are indistinguishable from cache misses

- **Where:** `AssetBlock.Infrastructure/Services/RedisCacheService.cs:24-28`
- **Severity:** High
- **Evidence:** Any Redis exception logs a warning and returns `null` — identical to a genuine miss. A Redis outage therefore becomes a silent full-traffic stampede onto PostgreSQL, and the four hand-rolled cache paths from [A8](#a8-catalog-and-review-handlers-hand-roll-json-caching-while-itypedcache-exists) each decide independently what that means. `asblock-backend/AGENTS.md:55` permits degradation only "when stale/missed data is acceptable", which requires the caller to be able to tell the difference — and it cannot.
- **Fix:** Return a discriminated result (`Hit` / `Miss` / `Unavailable`) or throw a typed exception, and add a circuit breaker so an outage stops hammering both Redis and the database.

### D6. Readme content from uploaded archives is sent to a third-party AI provider

- **Where:** `AssetBlock.Infrastructure/HostedServices/AssetProcessing/Handlers/ListingCopilotJobHandler.cs:73-87`, `AssetBlock.Infrastructure/Ai/OpenRouterAiGenerationProvider.cs:302-304`
- **Severity:** High
- **Evidence:** The handler passes `analysis.ReadmeContent` into the generation request and the OpenRouter provider sends it as the user message to an external API, with `Ai:OpenRouter:ZeroDataRetention` defaulting to `false` (`appsettings.json:160`). Sellers upload private, unpublished, paid intellectual property; its readme is the most sensitive descriptive text in the system and it currently crosses the trust boundary. `assetblock.md:54` scopes the copilot to a "безпечний manifest/README" — the intent was a *safe* subset, and the implementation sends raw content.
- **Fix:** Define and enforce what "safe" means: strip URLs, emails, licence keys, and code blocks before dispatch; cap length; and require `ZeroDataRetention: true` or route to local Ollama whenever readme content is included. Also disclose to the seller that their readme is processed remotely.

### D7. Job leases are never renewed although `RenewLease` exists

- **Where:** `AssetBlock.Domain/Abstractions/Services/IAssetProcessingJobStore.cs:24`, `AssetBlock.Infrastructure/HostedServices/AssetProcessing/AssetProcessingWorker.cs:168-331`
- **Severity:** High
- **Evidence:** The store exposes `RenewLease` and nothing calls it. The worker claims once and relies on `OperationTimeout` (4 min) staying inside `LeaseDuration` (5 min) — a one-minute margin defended only by handlers honouring cancellation. A handler blocked on unresponsive I/O outlives its lease, a second worker claims the same job, and both run: two malware scans, two AI calls, or two uploads for one job.
- **Fix:** Heartbeat `RenewLease` on a timer for RUNNING jobs and abandon work when renewal fails. The abstraction is already there.

### D8. Graceful shutdown waits indefinitely for in-flight jobs

- **Where:** `AssetBlock.Infrastructure/HostedServices/AssetProcessing/AssetProcessingWorker.cs:108-114`
- **Severity:** Medium
- **Evidence:** `await Task.WhenAll(_activeTasks.Values)` with no timeout. A handler ignoring cancellation hangs shutdown until the host force-kills the process, leaving jobs claimed with no clean release — which pairs badly with D7.
- **Fix:** Wrap in a shutdown budget (`Task.WhenAny` with a timeout), then mark outstanding jobs retryable and exit.

### D9. Processed outbox rows are never purged

- **Where:** `AssetBlock.Infrastructure/Persistence/Stores/OutboxStore.cs:85-97`, `AssetBlock.Infrastructure/Outbox/OutboxDispatcher.cs:11-14`
- **Severity:** Medium
- **Evidence:** `MarkProcessed` sets `ProcessedAt` and nothing deletes the row; there is no retention worker under `HostedServices/`. The table grows without bound and the claim query's index grows with it — the same shape as the refresh-token growth in [B21](#b21-refresh-tokens-accumulate-forever).
- **Fix:** One retention worker covering both: delete processed outbox rows and expired refresh tokens older than N days.

### D10. Retry and poll schedules have no jitter

- **Where:** `AssetBlock.Infrastructure/Outbox/OutboxDispatcher.cs:124-125` **(verified)**, `:15`, `:38`; `AssetBlock.Infrastructure/HostedServices/AssetProcessing/AssetProcessingWorker.cs:469-480`; `AssetBlock.Infrastructure/HostedServices/CheckoutReservationCleanupWorker.cs:18-20`
- **Severity:** Medium
- **Evidence:** Outbox retry is `2^attempt` capped at 3600 s with no randomization; the poll loop is a flat 2 s delay; asset-processing retry is pure `2^(attempt-1)`; checkout cleanup is a fixed 1-minute interval. When a shared dependency (SMTP, Stripe, storage) fails, every affected row retries in lockstep and hits it again simultaneously.
- **Fix:** Apply ±20% jitter to all four. This matters more with multiple instances, but retrying in lockstep against a recovering dependency is a problem with one.

### D11. Long outbox handlers can exceed the fixed 5-minute lease

- **Where:** `AssetBlock.Infrastructure/Outbox/OutboxDispatcher.cs:16`, `AssetBlock.Domain/Core/Constants/OutboxMessageTypes.cs:14`
- **Severity:** Medium
- **Evidence:** `LEASE_MINUTES = 5` with no renewal, while `EmailDispatchOutboxHandler` awaits SMTP at 30 s per send (`appsettings.json:60`) and the batch runs sequentially (D12). A batch of slow sends can walk past the lease.
- **Fix:** Renew the lease during handler execution, mirroring the D7 fix.

### D12. Outbox batches are processed strictly sequentially

- **Where:** `AssetBlock.Infrastructure/Outbox/OutboxDispatcher.cs:59-142` **(verified)**
- **Severity:** Medium
- **Evidence:** `foreach (var message in batch)` fully awaits each handler before the next, for batches up to 50. One slow SMTP send delays every subsequent notification, blob deletion, and email in that batch.
- **Fix:** Bounded `Parallel.ForEachAsync` (degree 4–8), preserving per-type ordering where a type requires it. With D11 this also reduces lease pressure.

### D13. AI HttpClients are configured with an infinite timeout

- **Where:** `AssetBlock.Infrastructure/DependencyInjection.cs:118-128`
- **Severity:** Medium
- **Evidence:** Both AI clients set `client.Timeout = Timeout.InfiniteTimeSpan`. Per-request budgets are enforced separately by `AiTimedHttp`, so this is deliberate layering — but any future code path using these clients without going through `AiTimedHttp` has no timeout at all.
- **Fix:** Set a generous ceiling (5 minutes) as a backstop while keeping per-request budgets authoritative.

### D14. Storage uploads set no content type and never use multipart

- **Where:** `AssetBlock.Infrastructure/Services/S3CompatibleObjectStore.cs:49-64`
- **Severity:** Medium
- **Evidence:** `PutObjectArgs` sets bucket, key, stream, and size only. Single-shot `PutObject` for objects up to the 250 MB limit (`appsettings.json:45`) means any network interruption restarts the entire transfer with no resume.
- **Fix:** Set content type from server-derived metadata and switch to multipart above 8–16 MB, which also gives per-part retry granularity.

### D15. Failed uploads leave orphaned objects for up to 24 hours

- **Where:** `AssetBlock.Infrastructure/HostedServices/StorageOrphanCleanupWorker.cs:18-19`, `:81-82`
- **Severity:** Medium
- **Evidence:** Cleanup runs daily and deliberately skips objects newer than 24 hours, so a failed upload's partial object occupies storage for a day. `asblock-backend/AGENTS.md:53` asks for partial and orphaned objects to be cleaned up at the point of failure.
- **Fix:** Delete the partial key in the upload failure path (best-effort, logged), keeping the sweeper as the backstop it should be rather than the primary mechanism.

### D16. Encryption key bytes live in a field for the process lifetime and are never zeroed

- **Where:** `AssetBlock.Infrastructure/Services/AesGcmEncryptionService.cs:17`, `:135-155`
- **Severity:** Medium
- **Evidence:** `private byte[]? _cachedKey` holds the decoded AES key indefinitely with no `CryptographicOperations.ZeroMemory` on shutdown, so it is present in any process dump or swap file for the app's lifetime.
- **Fix:** At minimum zero the array on dispose. Combined with the keyring from [C21](#c21-encryption-wire-format-carries-no-key-identifier), prefer per-operation retrieval from a protected source.

### D17. Decrypt allocates two heap buffers per chunk

- **Where:** `AssetBlock.Infrastructure/Services/AesGcmEncryptionService.cs:93-104`
- **Severity:** Medium
- **Evidence:** `new byte[chunkLength]` for both ciphertext and plaintext on every chunk at up to 1 MiB each. A 250 MB download allocates roughly 500 MB across ~250 iterations, all large-object-heap traffic, on a path that runs per concurrent download.
- **Fix:** Rent from `ArrayPool<byte>.Shared` and return in `finally`. The single biggest allocation win available in the backend.

### D18. A fresh `AesGcm` instance is constructed per chunk

- **Where:** `AssetBlock.Infrastructure/Services/AesGcmEncryptionService.cs:52-55`, `:99-101`
- **Severity:** Low
- **Evidence:** `using (var aes = new AesGcm(key, TAG_SIZE))` inside both loops, redoing key expansion for every chunk.
- **Fix:** Hoist one `AesGcm` per stream operation. The per-chunk nonce must stay fresh — that part is correct and must not change.

### D19. In-memory cache fallback allows unbounded entries with no TTL

- **Where:** `AssetBlock.Infrastructure/Services/MemoryCacheService.cs:25-29`, `AssetBlock.Infrastructure/DependencyInjection.cs:196-197`
- **Severity:** Medium
- **Evidence:** With Redis unconfigured, `SetString` without an expiration stores `(value, ExpiresAt: null)` in a `ConcurrentDictionary` forever. This is the default local path, so the dev experience is an unbounded in-process cache.
- **Fix:** Make the TTL parameter required and cap dictionary size with simple eviction.

### D20. Download rate-limit keys bypass `CacheKeys`

- **Where:** `AssetBlock.Infrastructure/Services/DownloadService.cs:15-16`, `:171`
- **Severity:** Medium
- **Evidence:** A local `DOWNLOAD_COUNTER_PREFIX = "dl"` and a hand-built `dl:{assetId}:{userId}:{windowKey}` format, while `asblock-backend/AGENTS.md:34` requires cache keys in the constants class. A two-character prefix is also collision-prone against future keys.
- **Fix:** Move construction into `CacheKeys` with the TTL and window semantics documented alongside it.

### D21. Ollama output is not schema-validated the way OpenRouter's is

- **Where:** `AssetBlock.Infrastructure/Ai/OllamaAiGenerationProvider.cs:251-262` versus `AssetBlock.Infrastructure/Ai/OpenRouterAiGenerationProvider.cs:305-313`
- **Severity:** Medium
- **Evidence:** OpenRouter requests `json_schema` with `"strict": true`; Ollama passes `"format"` with the parsed schema, a weaker hint the model can ignore. The same pipeline therefore offers two different guarantees depending on provider, and the weaker one is the local/offline path.
- **Fix:** Validate the Ollama response against `ResponseSchemaJson` in infrastructure and reject non-conformant output, so both providers meet the same contract regardless of model behaviour.

### D22. All 5xx responses are treated as retryable

- **Where:** `AssetBlock.Infrastructure/Ai/OpenRouterAiGenerationProvider.cs:321-323`, `AssetBlock.Infrastructure/Ai/OllamaAiGenerationProvider.cs:91-93`
- **Severity:** Medium
- **Evidence:** `IsRetryableStatus` returns true for anything `>= 500`. A 500 returned *after* the provider generated (and billed for) a completion causes full re-generation, so retries multiply cost and produce non-deterministic output for what the job layer treats as one operation.
- **Fix:** Retry transport failures, timeouts, 429, 502, 503, and 504; treat a bare 500 with a parsed body as terminal. Cap total attempts per job explicitly.

### D23. `DateTime.UtcNow` is used directly in ~59 production files while `TimeProvider` is registered

- **Where:** `AssetBlock.Infrastructure/DependencyInjection.cs:84` registers `TimeProvider.System` **(verified)**; direct static calls in ~59 production files including `Services/JwtTokenService.cs:27-28`, `:66`, `:76`, `:100`, `:107` **(verified)**, `Domain/Core/Primitives/BaseEntities/BaseEntity.cs:9` **(verified)**, `UseCases/Auth/ConfirmPasswordReset/ConfirmPasswordResetCommandHandler.cs:81` **(verified)**, `Outbox/OutboxDispatcher.cs:125`, `Services/DownloadService.cs:165`, `Email/EmailActionLinkProtector.cs`, `HostedServices/CheckoutReservationCleanupWorker.cs:74`
- **Severity:** Medium
- **Evidence:** `TimeProvider.System` is in the container and used by exactly three components — `AssetProcessingWorker`, `AnalyticsAggregationWorker`, and the rate limiters. Everything else calls the clock statically, including every security-sensitive expiry in the system: JWT lifetimes, refresh-token expiry, password-reset and email-action windows, review time windows, checkout reservation expiry, and download rate-limit windows. The cost shows up in the test suite, which resorts to real `Task.Delay` to observe time-dependent behaviour (D24). `BaseEntity.cs:9` is the most structural instance, since `CreatedAt` defaults to the static clock for every entity in the system.
- **Fix:** Inject `TimeProvider` into time-sensitive services in this order: `JwtTokenService`, `EmailActionLinkProtector`, `DownloadService`, then outbox and workers. Leave `BaseEntity` for last since it touches everything. The abstraction exists and is registered — this is adoption, not design.

### D24. Time-dependent tests use real wall-clock delays

- **Where:** `AssetBlock.Infrastructure.Tests/HostedServices/AssetProcessingWorkerTests.cs:106-109`, `AssetBlock.Infrastructure.Tests/Services/MemoryCacheServiceTests.cs:34-35`, `AssetBlock.Infrastructure.Tests/Outbox/OutboxDispatcherTests.cs:80-83`
- **Severity:** Low
- **Evidence:** `Task.Delay(80)` with a 200 ms cancellation token; a 1 ms TTL followed by `Task.Delay(50)`; a `Task.Delay(15)` inside an NSubstitute callback to make an elapsed-time assertion non-zero. These are the tests that will flake on a loaded CI runner, and they exist because of D23.
- **Fix:** Adopt `FakeTimeProvider` from `Microsoft.Extensions.TimeProvider.Testing` once D23 lands, and use `TaskCompletionSource` for worker synchronization instead of sleeping.

### D25. Two hosted services use the service-locator pattern

- **Where:** `AssetBlock.Infrastructure/HostedServices/StorageBucketEnsureHostedService.cs:18`, `:61-62`; `AssetBlock.Infrastructure/HostedServices/AssetProcessing/AssetProcessingJobRegistry.cs:38-43`
- **Severity:** Medium
- **Evidence:** The bucket service stores a raw `IServiceProvider` and resolves `IAssetStorageService` inside its loop; the job registry's `Execute` takes an `IServiceProvider` and calls `GetRequiredService<THandler>()`. Other workers correctly use `IServiceScopeFactory`, so this is both an inconsistency and a way of hiding real dependencies from the constructor.
- **Fix:** Inject `IServiceScopeFactory` in the bucket service and register handler factory delegates for the job registry.

### D26. MinIO and SeaweedFS adapters are identical delegating shells

- **Where:** `AssetBlock.Infrastructure/Services/MinioAssetStorageService.cs:11-38`, `AssetBlock.Infrastructure/Services/SeaweedFsAssetStorageService.cs:11-38`
- **Severity:** Nit
- **Evidence:** Both construct an `S3CompatibleObjectStore` from their respective options type and delegate every member identically. The real work is already shared in `S3CompatibleObjectStore`; these are two copies of the same 27-line shim.
- **Fix:** One `S3CompatibleAssetStorageService` registered twice with provider-specific options. Low priority since the duplication is inert.

### D27. Stripe events that are ignored produce no signal

- **Where:** `AssetBlock.Infrastructure/Services/StripePaymentService.cs:151-154`, `:169-171`
- **Severity:** Medium
- **Evidence:** Non-`checkout.session.completed` types and non-`paid` sessions return `null` with no log or metric, so "Stripe sent something we do not handle" and "nothing happened" look identical from outside. That is exactly the blind spot you do not want in payment reconciliation.
- **Fix:** Log the ignored event type and session status at Information with the event id, and add a counter by event type.

### D28. Stripe tests cover only failure paths

- **Where:** `AssetBlock.Infrastructure.Tests/Services/StripePaymentServiceTests.cs:63-77`
- **Severity:** Medium
- **Evidence:** Tests cover missing URLs, unreachable API, missing webhook secret, and bad signature — but nothing for a validly signed `checkout.session.completed`, and nothing for `MapPaidCheckout` amount/currency mapping. The money-handling path is the untested one.
- **Fix:** Add fixture-based tests for a verified completion event and for currency/amount edge cases (zero-decimal currencies, rounding). Pairs with the `UsdAmount` work in [A24](#a24-money-is-a-bare-decimal-with-a-separate-cents-pipeline-for-analytics).

### D29. Email-action outbox silently completes when the action is stale

- **Where:** `AssetBlock.Infrastructure/Outbox/EmailActionDispatchOutboxHandler.cs:51-64`
- **Severity:** Low
- **Evidence:** For a stale, consumed, or expired action the handler logs and returns, so the dispatcher marks it processed. Correct behaviour, but it also masks an enqueue/dispatch race — if this fires often something upstream is wrong and nothing says so.
- **Fix:** Increment a counter for skipped stale dispatches so the rate is observable.

### D30. `AssetBlobDeleteOutboxHandler` has no tests

- **Where:** `AssetBlock.Infrastructure/Outbox/AssetBlobDeleteOutboxHandler.cs:18-24`, no counterpart under `AssetBlock.Infrastructure.Tests/Outbox/`
- **Severity:** Low
- **Evidence:** The handler deserializes a payload and calls `storageService.Delete` — the irreversible operation in the outbox set — with no test for a malformed payload or a storage exception. Given D1, redelivery of this message type is also possible.
- **Fix:** Add failure-path tests (bad payload, storage throws) and assert the handler is safe to run twice for the same key.

### D31. `OrderCompletedOutboxHandler` only logs

- **Where:** `AssetBlock.Infrastructure/Outbox/OrderCompletedOutboxHandler.cs:17-29`
- **Severity:** Low
- **Evidence:** Deserializes the payload and writes a log line; the comment notes notifications are separate rows. Each completed order therefore pays a database write, a claim, a lease, and a dispatch cycle to produce one log entry.
- **Fix:** Either drop the message type or have it persist an auditable consumer record that justifies the round-trip.

### D32. ClamAV signature freshness uses the static clock

- **Where:** `AssetBlock.Infrastructure/Services/ClamAvContentMalwareScanner.cs:320-323`
- **Severity:** Low
- **Evidence:** `if (builtAt > DateTimeOffset.UtcNow.AddMinutes(30)) return false;` inside signature-database age validation — a health decision made against an untestable clock. An instance of D23, called out separately because it gates the malware scanner.
- **Fix:** Inject `TimeProvider` here as part of the D23 rollout.

---

## E. Frontend — BFF, auth & contracts

Two findings this lane raised are covered above because they are two halves of one backend/frontend issue: the SignalR token exposure is [C1](#c1-signalr-authentication-puts-the-access-jwt-in-a-query-string-and-hands-it-to-page-javascript), and logout not revoking server-side is [C2](#c2-there-is-no-logout-endpoint-so-logging-out-does-not-invalidate-the-refresh-token).

One thing to state up front, because it is the strongest single property of this layer: **all 43 mutating BFF handlers call `assertSameOrigin` before any side effect.** Not one omits it. That is genuinely rare and worth protecting with E9 rather than trusting to continued vigilance.

### E1. Token refresh is not persisted in two authenticated Route Handlers

- **Where:** `app/api/account/library/route.ts:16`, `app/api/assets/[id]/versions/route.ts:13`, `lib/server/fetch-backend.ts:24-25`
- **Severity:** High
- **Evidence:** Both routes pass `{ persistRefreshedTokens: false }`. A refresh can therefore succeed in-memory, serve the request, and then discard the rotated tokens because `setAuthCookies` is skipped. The flag exists because Server Components cannot write cookies — but these are Route Handlers, which can. The user's session silently fails to extend, and the old refresh token is consumed for nothing.
- **Fix:** Remove the flag from both handlers and reserve it strictly for Server Component paths. Better, add a `withAuthedBffRoute` wrapper (see E9) that always persists, so `app/api/**` cannot opt out by accident.

### E2. Concurrent refresh has no single-flight lock

- **Where:** `lib/server/fetch-backend.ts:33-38`, `:58-62`, `lib/server/refresh-session.ts:37-45`
- **Severity:** High
- **Evidence:** Every parallel `fetchBackend` call independently invokes `tryRefreshFromCookies`, with no mutex or dedupe. A page issuing several BFF requests after the access token expires fires several simultaneous rotations; each rotation invalidates the previous refresh token, so all but one lose the race and the user can be logged out by nothing more than loading a page with parallel queries. This gets worse once [C3](#c3-no-refresh-token-reuse-detection) is implemented, because the losing requests would then look like token theft and revoke every session.
- **Fix:** Hold a module-level in-flight `refreshPromise` in `refresh-session.ts` keyed by the refresh token, so concurrent callers await one exchange. Do this *before* C3, or C3 will cause mass logouts.

### E3. Nine mutating routes forward raw JSON bodies with no validation

- **Where:** `app/api/account/me/route.ts:20-28`, `app/api/seller/assets/[id]/route.ts:24-28`, `app/api/account/socials/route.ts:10-14`, `app/api/admin/categories/route.ts:10-14`, `app/api/admin/categories/[id]/route.ts:11-15`, `app/api/admin/tags/route.ts:10-14`, `app/api/admin/tags/[id]/route.ts:11-15`, `app/api/seller/assets/[id]/tags/route.ts:11-15`, `app/api/reviews/assets/[assetId]/reviews/route.ts:11-17`
- **Severity:** High
- **Evidence:** Each does `await request.text()` and forwards the body unchanged. `asblock-frontend/AGENTS.md:35` requires BFF routes to validate input. The sharpest part: `lib/admin/admin-schemas.ts:9-27` already defines `adminCategoryCreateSchema` and `adminTagCreateSchema`, and the admin routes do not import them. The schemas were written and then not wired up.
- **Fix:** `safeParse` in all nine, starting with the four admin routes where the schemas already exist. The backend validates too, so this is defense-in-depth and error-quality rather than the only line of defense — but it is the layer that gives the user a useful 400 instead of a proxied one.

### E4. Failed refresh leaves stale auth cookies in the generic BFF path

- **Where:** `lib/server/fetch-backend.ts:58-65`, contrast `app/api/auth/refresh/route.ts:16-18`
- **Severity:** Medium
- **Evidence:** On a 401 after a failed refresh, `fetchBackend` returns the backend's 401 but never calls `clearAuthCookies`. `/api/auth/refresh` does clear them. So the browser keeps cookies that are known-dead, and every subsequent request repeats the doomed refresh attempt until something else clears them.
- **Fix:** Call `clearAuthCookies(cookieStore)` when a required-auth refresh fails inside `fetchBackend`, matching what the dedicated refresh route already does.

### E5. Dynamic `[id]` params are forwarded without UUID validation

- **Where:** `app/api/seller/assets/[id]/route.ts:6-10`, `app/api/admin/reviews/[id]/route.ts:9-11`, `app/api/seller/asset-versions/[id]/listing-copilot/route.ts:7-11`, and the same shape in ~25 more dynamic routes; also `app/api/payments/checkout/[checkoutIntentId]/status/route.ts:10-18` and the `versionId` query param at `app/api/assets/[id]/download/route.ts:9-14`
- **Severity:** Medium
- **Evidence:** `const { id } = await context.params` then interpolation into the backend path. `encodeURIComponent` is applied, which prevents path injection — so this is about error quality and wasted round-trips rather than a security hole. The checkout-status case is the notable one: `lib/payments/payments-schemas.ts` defines `checkoutIntentId: z.string().uuid()` and the route checks only `.trim()`.
- **Fix:** Add `lib/server/bff-params.ts` with `parseUuidParam(name, value)` returning a 400 problem response, and use it in every `[id]` handler plus the two query params.

### E6. Five list routes pass entire query strings through unvalidated

- **Where:** `app/api/seller/listings/route.ts:10-12`, `app/api/seller/collections/route.ts:13-15`, `app/api/seller/bundles/route.ts:13-15`, `app/api/account/notifications/route.ts:7-9`, `app/api/admin/audit-logs/route.ts:7-9`
- **Severity:** Medium
- **Evidence:** `url.searchParams.toString()` concatenated straight into the backend path. Combined with the missing backend validators from [A6](#a6-commands-and-queries-missing-validators-entirely) — `GetMyListingsQuery` has no `PagedRequestValidator` — an arbitrary `pageSize` travels from the browser through the BFF to an unclamped store query. Note `lib/analytics/analytics-bff-params.ts` already implements the correct pattern for the analytics routes.
- **Fix:** Generalize the analytics helper into `buildValidatedListQuery(url, schema)` and apply it to all five. Fix A6 as well — neither layer should be the only one clamping.

### E7. No timeout on any server-side backend fetch

- **Where:** `lib/server/fetch-backend.ts:56`, `lib/server/auth-backend.ts:18-23`, `app/api/auth/password-reset/request/route.ts:43-48`
- **Severity:** Medium
- **Evidence:** Every backend `fetch(...)` sets `cache: 'no-store'` but no `AbortSignal.timeout(...)`. A hung backend therefore hangs the Next.js server function until the platform kills it, consuming a worker slot per stuck request. Same class of gap as [D13](#d13-ai-httpclients-are-configured-with-an-infinite-timeout) on the backend side.
- **Fix:** Add a shared timeout wrapper in `fetch-backend.ts` defaulting to ~30 s, with a longer explicit budget for upload and export routes.

### E8. `fetchBackend` accepts absolute URLs in `path`

- **Where:** `lib/server/fetch-backend.ts:28`
- **Severity:** Medium
- **Evidence:** `const url = path.startsWith('http') ? path : ...` bypasses base-URL joining entirely. No current caller passes an absolute URL, so this is a latent footgun rather than a live SSRF — but it means the helper will silently send cookies and bearer tokens to any host a future caller names, which is exactly the kind of thing that gets introduced by an agent following a "just pass the URL" instinct.
- **Fix:** Reject anything not starting with `/api/` and throw. One guard closes the class permanently.

### E9. Route Handler boilerplate is duplicated across ~55 files

- **Where:** `assertSameOrigin` in 43 files, `await cookies()` in ~55, `forwardBackendResponse` in ~55; representative: `app/api/seller/collections/route.ts:11-17`, `app/api/account/notifications/[id]/read/route.ts:5-15`
- **Severity:** Medium
- **Evidence:** The same 4–8 line preamble — origin check, cookie store, authorized fetch, forward — repeated in every handler, roughly 200+ duplicated lines. Today all 43 mutating routes get the CSRF check right, but that correctness is maintained by copy-paste discipline across 55 files, and E1/E3/E5 are all instances of one handler in the set diverging from the others.
- **Fix:** Add `lib/server/bff-route.ts` exporting `withAuthedProxy({ method, path, validate?, sameOrigin? })`. This is the structural fix that makes E1, E3, E5, E6, E7, and E11 unrepeatable rather than individually patched, and it is why I would do it before the individual fixes.

### E10. Login and register BFF routes have no rate limiting

- **Where:** `app/api/auth/login/route.ts:14-33`, `app/api/auth/register/route.ts:14-31`, contrast `app/api/auth/password-reset/request/route.ts:35-38`
- **Severity:** Medium
- **Evidence:** Password reset uses `enforceBffRateLimit`; login and register have same-origin checks but no throttling. The backend does rate-limit these (`RateLimitingConstants.Policies.AUTH_LOGIN`), so this is a second layer rather than the only one — but the BFF is where per-email keying is easy, and it is also where [C14](#c14-auth-rate-limits-share-one-bucket-for-all-clients-with-no-resolvable-ip)'s "unknown IP" problem does not apply.
- **Fix:** Apply `enforceBffRateLimit` with per-IP and per-email keys to both.

### E11. Authenticated JSON responses lack `Cache-Control: no-store`

- **Where:** `lib/server/bff-http.ts:68-80`, `app/api/account/me/route.ts:11-12`, `app/api/account/library/route.ts:18`
- **Severity:** Medium
- **Evidence:** `forwardBackendResponse` forwards only `content-type` and `content-disposition`. Private user data — profile, purchase library — goes back with no cache directive, so an intermediate proxy is free to apply heuristic caching. The `signalr-access` route gets this right at `:28-30`, which shows the pattern is understood, just not centralized.
- **Fix:** Set `Cache-Control: private, no-store` and `Vary: Cookie` inside `forwardBackendResponse` for authenticated proxies.

### E12. Asset download uses the generic forwarder instead of the download-specific one

- **Where:** `app/api/assets/[id]/download/route.ts:16-18`, contrast `app/api/seller/analytics/sales/export/route.ts:18`
- **Severity:** Medium
- **Evidence:** Uses `forwardBackendResponse(res)` where `forwardBackendDownloadResponse(res)` exists in `lib/server/bff-http.ts` and is used by the CSV export. So the paid-asset binary download is the one response missing download hardening and `no-store`.
- **Fix:** Switch to `forwardBackendDownloadResponse`.

### E13. CSRF defense is Origin-only

- **Where:** `lib/server/bff-http.ts:50-64`
- **Severity:** Medium
- **Evidence:** `assertSameOrigin` reads only the `Origin` header and returns 403 when it is absent. Failing closed on a missing header is the right call and makes this materially safer than most implementations — the gap is purely that there is no second signal.
- **Fix:** Reject `Sec-Fetch-Site: cross-site` on mutating methods and accept a same-origin `Referer` as a fallback. Both are cheap additions to one function.

### E14. Multipart uploads buffer the entire archive in server memory

- **Where:** `app/api/seller/upload/route.ts:22-38`, `app/api/seller/assets/[id]/versions/route.ts:23-43`
- **Severity:** Medium
- **Evidence:** `await request.formData()` materializes the whole upload and rebuilds `FormData` to proxy it. The 250 MiB limit is enforced client-side only (`lib/seller/seller-multipart-schemas.ts:40-41`), so a crafted request can push a large body straight into the Next.js server's memory. Note the backend has the same buffering concern on its webhook path ([C4](#c4-stripe-webhook-buffers-the-entire-request-body-with-no-size-limit)).
- **Fix:** Stream the request body to the backend rather than reconstructing `FormData`, and enforce `Content-Length` server-side before reading. Add `export const maxDuration` while you are there (E19).

### E15. No typed environment module

- **Where:** `lib/http/api-config.ts:5-29`, `.env.example:4-10`
- **Severity:** Low
- **Evidence:** `getServerApiBaseUrl()` throws at first call rather than at startup, so a misconfigured deployment boots successfully and fails on the first user request. `ASSETBLOCK_ANALYTICS_BFF_SIGNING_SECRET` is optional with a silent telemetry drop (`lib/analytics/analytics-bff-signature.ts:32-39`) — reasonable for telemetry, but it means a missing secret is indistinguishable from working.
- **Fix:** Add `lib/env.ts` with a Zod-validated schema parsed at module load, imported from the root layout so failures are boot-time. The backend already does exactly this with its 21 options validators — this is the frontend equivalent of a pattern the project already believes in.

### E16. `NEXT_PUBLIC_API_BASE_URL` is a server-side fallback

- **Where:** `lib/http/api-config.ts:21-23`
- **Severity:** Low
- **Evidence:** `const base = serverFirst || publicFallback` lets server-side BFF calls fall back to the browser-facing URL when `ASSETBLOCK_API_BASE_URL` is unset. In a split deployment that routes server traffic out through the public edge and back, which is slower and can bypass internal network assumptions.
- **Fix:** Require `ASSETBLOCK_API_BASE_URL` on server paths and fail the build when only the public variable is set. Natural to fold into E15.

### E17. Refresh cookie is not path-scoped

- **Where:** `lib/server/auth-cookies.ts:25-37`
- **Severity:** Low
- **Evidence:** Both cookies correctly use `httpOnly`, `sameSite: 'lax'`, and `secure` in production — the flags are right. The refresh cookie is scoped to `path: '/'` though, so it is attached to every request to the app rather than only the routes that need it.
- **Fix:** Scope the refresh cookie to `/api/auth` and consider the `__Host-` prefix in production. Confirm the Server Component refresh path still reads it before shipping.

### E18. Auth error forwarding bypasses the centralized helpers

- **Where:** `app/api/auth/login/route.ts:35-39`, `app/api/auth/register/route.ts:33-37`, `app/api/auth/email-verification/confirm/route.ts:62-67`
- **Severity:** Low
- **Evidence:** Failed auth constructs `new Response(JSON.stringify(data), ...)` forwarding the raw backend payload, while success paths use `problemResponse` / `invalidJsonResponse`. So the error contract for the most security-sensitive routes is the one place it is hand-rolled.
- **Fix:** Route all auth failures through `parseApiErrorBody` plus a single `forwardBackendProblem(res)` helper in `bff-http.ts`.

### E19. Long-running routes declare no runtime configuration

- **Where:** `app/api/seller/upload/route.ts:18`, `app/api/seller/assets/[id]/versions/route.ts:18` — no `export const runtime` or `maxDuration` anywhere under `app/api/**`
- **Severity:** Low
- **Evidence:** Large multipart proxies rely entirely on platform defaults, which on most hosts is well under what a 250 MiB upload needs.
- **Fix:** Add `export const maxDuration = 300` (or the platform equivalent) to the upload and version routes.

### E20. `AbortSignal` is propagated on exactly one route

- **Where:** `app/api/seller/analytics/sales/export/route.ts:16`
- **Severity:** Low
- **Evidence:** Only the sales export passes `signal: request.signal`. Every other proxy keeps working against the backend after the client has disconnected, so abandoned navigations still cost a full backend round-trip.
- **Fix:** Thread `request.signal` through the shared helper from E9 so all proxies get it by default.

### E21. `account/me` PATCH reports a schema problem as a JSON problem

- **Where:** `app/api/account/me/route.ts:20-24`
- **Severity:** Low
- **Evidence:** `await request.text()` inside `try/catch` returns `invalidJsonResponse()` ("must be valid JSON") for a read failure, which is not what went wrong and misleads the client.
- **Fix:** Use `request.json()` for the parse error and a Zod `safeParse` for the shape error, returning distinct problems. Folds into E3.

### E22. Route Handler test coverage is about 3%

- **Where:** `app/api/auth/auth-routes.test.ts` and `app/api/seller/asset-versions/[id]/listing-copilot/route.test.ts`, against ~61 route files under `app/api/**`
- **Severity:** Low
- **Evidence:** Two handlers have tests; 59 do not. Helper-level tests exist (`lib/server/bff-http.test.ts`, `lib/server/fetch-backend.test.ts`) and cover the shared logic well, which is why this is Low rather than higher — but nothing verifies that an individual handler actually calls the shared logic, which is precisely how E1, E3, and E12 happened.
- **Fix:** After E9, one parameterized suite over the route table asserting CSRF rejection, 401 cookie clearing, and Zod 400s for every registered route. A table-driven test scales to 61 routes; 61 hand-written files do not.

### E23. Listing-copilot route test mocks the entire auth stack

- **Where:** `app/api/seller/asset-versions/[id]/listing-copilot/route.test.ts:15-17`
- **Severity:** Nit
- **Evidence:** `vi.mock('@/lib/server/backend-authorized')` replaces the real refresh and cookie logic, so the test cannot catch an auth-wiring mistake — and there is no negative case for a missing session. `auth-routes.test.ts` takes the better approach of stubbing global `fetch`.
- **Fix:** Follow the `auth-routes.test.ts` style and add a 401 case.

### E24. `proxy.ts` role parsing is intentionally unverified

- **Where:** `proxy.ts:27-52`
- **Severity:** Nit
- **Evidence:** The admin role is read from the unsigned JWT payload in a cookie without signature verification. The comment says "Coarse UX guard only", which is accurate and correct — the BFF and backend remain authoritative. Recording it only so nobody later mistakes it for an authorization boundary.
- **Fix:** No code change. Keep the comment, and make "no security decisions in `proxy.ts`" an explicit line in `asblock-frontend/AGENTS.md` so the constraint survives the next contributor.

---

## F. Frontend — UI, state, performance & a11y

### F1. An interactive `<Link>` is nested inside a `<button>`

- **Where:** `components/notifications/notification-bell.tsx:289-320`
- **Severity:** High
- **Evidence:** Each notification row is a `<button onClick={toggleRead}>` containing an inner `<Link href={href}>Open</Link>`. Interactive content inside a button is invalid HTML; browsers resolve it inconsistently, and keyboard and screen-reader users get an ambiguous control where activation may toggle read state, navigate, or both.
- **Fix:** Restructure the row as a container with two sibling controls — a link for navigation and an icon button for read/unread — rather than one nested inside the other.

### F2. Private cache clearing misses the analytics and admin namespaces

- **Where:** `lib/query/clear-user-scoped-queries.ts:14-20`, `lib/analytics/analytics-query.ts:16-17`, `lib/admin/admin-query.ts:4-7`
- **Severity:** High
- **Evidence:** `clearPrivateUserQueries` clears seller, library, account, and notification keys. `analyticsKeys` and `adminKeys` are never removed, so after logout or a user switch the cache still holds the previous user's seller revenue figures and admin audit-log data — and TanStack Query will serve them from cache to the next user on the same browser. `asblock-frontend/AGENTS.md:34` requires user-scoped caches to be cleared on session change.
- **Fix:** Add `analyticsKeys.all` and `adminKeys.all` to both `clearPrivateUserQueries` and the `syncQueryCacheAfterAuth` invalidation. Then restructure so the list cannot go stale: derive it from a registry that every `*-query.ts` module registers into, so a new private namespace is covered by construction.

### F3. The library page is silently capped at 100 purchases

- **Where:** `app/api/account/library/route.ts:7-10`, `components/library/library-page-client.tsx:95-100`
- **Severity:** High
- **Evidence:** The BFF hardcodes `pageSize: '100'` and the UI does `purchases.map(...)` with no pagination, despite `totalCount` being present in the DTO. A user with more than 100 purchases simply cannot see the rest of what they paid for, and nothing tells them.
- **Fix:** Add pagination or infinite scroll, thread `page`/`pageSize` through the BFF, and show "showing X of Y" so truncation is never silent. Add the composite index from [B3](#b3-purchases-library-filters-and-sorts-on-columns-with-no-matching-index) at the same time, since paging this query without it will get slow.

### F4. The user profile is fetched twice per request

- **Where:** `app/users/[username]/page.tsx:22-24`, `:44-49`, `lib/server/user-profile-server.ts:8-23`
- **Severity:** High
- **Evidence:** `generateMetadata` and the page component both call `fetchPublicProfileByUsername`, which is a plain `fetch` — unlike the asset-detail path, which correctly wraps its loader in React `cache()`. Every profile view therefore costs two identical backend round-trips.
- **Fix:** Wrap `fetchPublicProfileByUsername` in `cache()`, following the existing `getAssetDetailCached` pattern. One-line fix, and it is worth auditing the other server loaders for the same omission.

### F5. Major feature areas have no component tests

- **Where:** `components/admin/**`, `components/library/**`, `components/account/**`, `components/auth/sign-in-form.tsx`, `components/sell/analytics/sell-analytics-dashboard.tsx`, `components/reviews/post-checkout-review-banner.tsx` — all zero tests
- **Severity:** High
- **Evidence:** About 30 test files exist repo-wide, and the untested set is precisely the risky set: admin destructive actions, library download and version selection, post-checkout polling, and sign-in. The processing and copilot panels are well tested, so the capability and patterns exist — they just have not reached these areas.
- **Fix:** In priority order: sign-in and session transitions, post-checkout polling, library download/version selection, then admin destructive actions. Build the shared harness from F26 first so these tests do not each invent their own wrapper.

### F6. Four god components carry 400–650 lines each

- **Where:** `components/sell/sell-my-collections.tsx:1-675` (~646 lines), `components/sell/sell-my-bundles.tsx:1-599` (~567), `components/sell/analytics/sell-analytics-dashboard.tsx:78-215` (~445), `components/admin/admin-audit-logs-section.tsx:61-443` (~426)
- **Severity:** Medium
- **Evidence:** Each combines data fetching, multiple React Hook Form instances, five or more mutations, local formatting helpers, and the entire JSX tree. `sell-my-collections.tsx` alone owns list and detail queries, two forms, ~6 mutations, reorder UI, and publish/archive flows. These are also, not coincidentally, among the least-tested files.
- **Fix:** Apply one consistent split per file: a `use*Controller` hook holding queries and mutations, plus presentational children. Doing `sell-my-collections.tsx` first establishes the pattern, and `sell-my-bundles.tsx` then follows mechanically because it mirrors the same structure.

### F7. Catalog browse filters are not URL-synced

- **Where:** `app/assets/page.tsx:31-32`, `components/collections/collections-browse-page.tsx:16-18`, `components/bundles/bundles-browse-page.tsx`
- **Severity:** Medium
- **Evidence:** Filters live in `useState` only, so a filtered catalog view cannot be linked, bookmarked, or restored on back-navigation. The analytics dashboard and sell tabs do this correctly via `lib/analytics/analytics-range.ts`, and `asblock-frontend/AGENTS.md` calls for URL-synced filter state — so the convention exists and the public-facing pages are the ones missing it.
- **Fix:** Add `lib/catalog/catalog-url-state.ts` mirroring the analytics helper and sync filters plus page number to the query string in all three browse pages.

### F8. Public catalog, home featured, and bundle detail are client-rendered

- **Where:** `app/assets/page.tsx:1-46`, `app/page.tsx:16` with `components/featured-assets-section.tsx:148-153`, `app/bundles/[id]/page.tsx:10-15` with `components/bundles/bundle-detail-view.tsx:39-46`
- **Severity:** Medium
- **Evidence:** The three highest-traffic public entry points fetch after hydration rather than server-rendering initial data. `app/assets/page.tsx` is entirely `'use client'`; the home page's featured block queries on mount; bundle detail loads client-side and its metadata is the generic string `Bundle · AssetBlock`. This costs LCP on exactly the pages that need it most and gives search engines an empty shell plus a placeholder title.
- **Fix:** Keep a Server Component shell that reads URL params, prefetches via the existing server helpers, and passes `initialData` to a thin client component. The asset-detail page already demonstrates the pattern. Bundle detail also needs real `generateMetadata`.

### F9. Asset detail runs two independent server reads sequentially

- **Where:** `app/assets/[id]/page.tsx:33-39`
- **Severity:** Medium
- **Evidence:** After the detail fetch resolves, `getAssetReviewsCached` and `fetchPaymentsCapabilitiesServer` are awaited one after the other despite being independent. The page pays the sum of two latencies instead of the max.
- **Fix:** `await Promise.all([getAssetReviewsCached(id), fetchPaymentsCapabilitiesServer()])`. Same shape as [B10](#b10-seller-analytics-overview-awaits-ten-sequential-round-trips) on the backend.

### F10. Processing jobs poll every 5 seconds while SignalR invalidates the same keys

- **Where:** `lib/seller/seller-processing-query.ts:18-39`, `hooks/use-asset-processing-subscription.ts:37-50`, `components/providers/authenticated-processing-listener.tsx:6-9`
- **Severity:** Medium
- **Evidence:** Active jobs set `refetchInterval: 5000` while hub events call `invalidateQueriesInBackground` on the identical processing and seller keys. Two independent mechanisms drive the same refetch, so a connected client polls needlessly — and the polling exists as a fallback for when the hub is *not* connected, which the code never checks.
- **Fix:** Gate `refetchInterval` on subscription state: poll only while the hub is disconnected, and let SignalR drive updates when it is connected. The connection status is already available from the subscription hook.

### F11. SignalR notification events invalidate the whole notifications namespace

- **Where:** `components/notifications/notification-bell.tsx:155-157`
- **Severity:** Medium
- **Evidence:** The hub callback runs `invalidateQueries({ queryKey: notificationsKeys.all })`, refetching both the unread count and the full inbox on every pushed notification — including when the dropdown is closed and the inbox is not rendered.
- **Fix:** Always invalidate `notificationsKeys.unread()`; invalidate the inbox only when the dropdown is open. Better still, patch the unread count with `setQueryData` from the pushed payload and skip the round-trip entirely.

### F12. Publish-version mutation invalidates the buyer library cache

- **Where:** `components/sell/publish-version-form.tsx:72-79`
- **Severity:** Medium
- **Evidence:** After a seller uploads a version, the mutation invalidates `libraryKeys.all`. Nothing in the buyer's library has changed at that moment — the version is not processed or published yet — so this is a guaranteed-wasted refetch of a paginated list on every publish.
- **Fix:** Drop `libraryKeys.all` here. Library invalidation belongs on entitlement and checkout events, where it is already handled.

### F13. Checkout status uses an inline query key defined in the component

- **Where:** `components/reviews/post-checkout-review-banner.tsx:41-48`
- **Severity:** Medium
- **Evidence:** `queryKey: ['checkout-status', context?.checkoutIntentId]` is declared inline alongside the polling logic, breaking the `lib/**/*-query.ts` convention that every other feature follows. It is therefore invisible to `clearPrivateUserQueries` — the same class of problem as F2, and this key holds payment state.
- **Fix:** Add `lib/payments/checkout-query.ts` exporting `checkoutKeys.status(id)` and `useCheckoutStatusQuery`, and include it in the private-cache clearing list.

### F14. Contract bounds are hand-copied into Zod schemas

- **Where:** `lib/seller/seller-schemas.ts:13-48`, `lib/seller/seller-copilot-schemas.ts:12-25`, versus `AssetBlock.Domain/Core/Constants/ListingSuggestionBounds.cs:5-7` and the validators in [A10](#a10-validation-bounds-are-magic-numbers-while-domain-constants-exist)
- **Severity:** Medium
- **Evidence:** `.max(500)` for title, `.max(5000)` for description, and `.max(10)` for tags are typed literally in the frontend schemas. The same three numbers exist as C# constants, as inline validator literals, and here — three copies across two languages. A backend bound change produces a frontend that accepts input the API will reject, with no compile error anywhere.
- **Fix:** Short term, centralize the numbers in `lib/contracts/marketplace-bounds.ts` so the frontend has one copy, and fix A10 so the backend has one. Longer term, generate types and bounds from the OpenAPI document the backend already publishes (`app/docs/page.tsx:57-59`) — the schema exists and is unused, which makes codegen a wiring task rather than new infrastructure.

### F15. Library responses are cast rather than parsed

- **Where:** `lib/library/library-query.ts:53-54`, `:110`
- **Severity:** Medium
- **Evidence:** `const data = parsed as PagedPurchaseLibraryDto` and `as AssetVersionSummaryApi[]` with no runtime validation, while the seller and collection features define Zod schemas for their responses. `asblock-frontend/AGENTS.md:60` forbids broad casts. The library is the feature where a silent shape mismatch means a paying user cannot download what they bought.
- **Fix:** Add `libraryPurchasesResponseSchema` and `assetVersionsResponseSchema` in `lib/library/library-schemas.ts` and parse both payloads.

### F16. Star ratings are implemented three different ways

- **Where:** `components/assets/asset-card.tsx:76-86`, `components/featured-assets-section.tsx:18-40`, `components/assets/asset-detail-hero.tsx:40-57`
- **Severity:** Medium
- **Evidence:** Three separate implementations — a Lucide icon loop, inline SVG, and half-star logic — with three different rounding behaviours, so the same asset can display a different star count on the card and the detail page. Only the featured version has an accessible label (`:21`); the catalog card's star group has none, making the rating invisible to screen readers.
- **Fix:** Extract `components/assets/star-rating.tsx` taking `value`, `size`, and `showLabel`, with `aria-label={`Rating: ${value} out of 5`}` on the container and stable keys. Replace all three. This fixes a duplication, a consistency, and an accessibility finding in one change.

### F17. Two asset card implementations exist

- **Where:** `components/assets/asset-card.tsx:16-101`, `components/featured-assets-section.tsx:65-130`
- **Severity:** Medium
- **Evidence:** The featured section defines its own `AssetCard` with near-identical layout, pricing, and tag markup rather than reusing the shared component. Pricing display logic is therefore duplicated on two surfaces that a user sees within seconds of each other.
- **Fix:** Add a layout variant (`compact` | `carousel`) to the shared `AssetCard` and delete the local copy.

### F18. `components/ui/empty` exists but nothing uses it

- **Where:** `components/ui/empty.tsx:5-94`, versus hand-built empty states in `components/library/library-page-client.tsx:103-115`, `components/sell/sell-my-listings.tsx:122-128`, `components/notifications/notification-bell.tsx:272-278`
- **Severity:** Medium
- **Evidence:** The shadcn `Empty*` primitives are exported and imported by zero features; each feature hand-builds centered empty markup instead. So the abstraction exists, is maintained, and is dead — while the duplication it would remove keeps growing.
- **Fix:** Add `components/shared/query-empty-state.tsx` composing `Empty` with icon, title, and CTA, then migrate the library, sell, and notification empty blocks onto it.

### F19. No skip-to-main-content link, with a fixed header

- **Where:** `app/layout.tsx:18-35`, `components/layout/site-main.tsx:10-11`, `components/site-header.tsx:48-55`
- **Severity:** Medium
- **Evidence:** The header is `fixed` and main is offset with `pt-28`, but there is no skip link. Keyboard users must tab through the entire header and nav on every page before reaching content.
- **Fix:** Add a visually-hidden-until-focused "Skip to main content" link as the first focusable element in the root layout, targeting `id="main-content"` on `SiteMain`.

### F20. Route paths are hardcoded across components

- **Where:** `components/sell/sell-my-listings.tsx:165-182`, `components/library/library-page-client.tsx:55`, `lib/notifications/notification-ui.ts:101-103`, and many more
- **Severity:** Medium
- **Evidence:** Template literals like `` `/sell/assets/${id}/edit` `` and `` `/login?returnUrl=…` `` are repeated across components and lib modules. A route rename becomes a full-text search with no type checking, and the `returnUrl` construction is duplicated where it matters for auth redirects.
- **Fix:** Add `lib/routes.ts` with typed builders (`routes.sellAssetEdit(id)`, `routes.login(returnUrl)`) and replace the inline paths. This is the frontend counterpart to the backend's `ApiRoutes` ([C27](#c27-hardcoded-route-literal-bypasses-apiroutes)).

### F21. Collection status uses a loose `string` for badge mapping

- **Where:** `components/sell/sell-my-collections.tsx:67-70`, `:366-390`
- **Severity:** Medium
- **Evidence:** `statusBadgeVariant(status: string)` compares against `'PUBLISHED'` and `'ARCHIVED'` string literals without typing against `collectionStatusResponseSchema`, so a backend status rename or a typo fails silently into the default branch.
- **Fix:** Type the parameter as `CollectionStatus` from `lib/collections/collection-schemas.ts` and share a `getCollectionStatusBadge()` helper.

### F22. Admin audit logs reimplement date formatting

- **Where:** `components/admin/admin-audit-logs-section.tsx:39-49`, versus `lib/format-date.ts:27-37`
- **Severity:** Medium
- **Evidence:** A local `formatTime(iso)` using `toLocaleString(undefined, …)` while shared date helpers already centralize formatting — so audit timestamps render in a different format from every other date in the app.
- **Fix:** Add `formatDateTimeLocal(iso)` to `lib/format-date.ts` and use it here. Same for the relative-time case: `notification-bell.tsx:10` imports `formatDistanceToNow` from `date-fns` directly (`:308`) rather than going through the shared module — wrap it as `formatRelativeTime(iso)`.

### F23. Duplicate email schema outside the auth module

- **Where:** `components/auth/forgot-password-view.tsx:18-20`, versus `lib/auth/schemas.ts` and `app/api/auth/password-reset/request/route.ts:14`
- **Severity:** Medium
- **Evidence:** Forgot-password defines an inline `z.object({ email: … })` instead of reusing the shared auth schemas, so email validation rules can diverge between sign-in, registration, and password reset.
- **Fix:** Export `emailFieldSchema` and `passwordResetRequestSchema` from `lib/auth/schemas.ts` and use them in both the view and the BFF route.

### F24. Currency formatting is split across two modules

- **Where:** `lib/format-currency.ts:4-12`, `lib/analytics/analytics-format.ts:5-75`
- **Severity:** Low
- **Evidence:** The catalog uses `formatUsdWhole`; analytics defines its own `Intl.NumberFormat` instances for cents and dollars. Two rounding behaviours for money in one app — and the backend has the same split ([A24](#a24-money-is-a-bare-decimal-with-a-separate-cents-pipeline-for-analytics)).
- **Fix:** Extend `lib/format-currency.ts` with cents-aware helpers and have the analytics formatter delegate to it.

### F25. Polling intervals and page sizes are inline magic numbers

- **Where:** `components/reviews/post-checkout-review-banner.tsx:20-21`, `lib/seller/seller-processing-query.ts:18`, `app/api/account/library/route.ts:9`
- **Severity:** Low
- **Evidence:** `POLL_MS = 2000`, `PROCESSING_POLL_INTERVAL_MS = 5000`, and `pageSize: '100'` are declared where they are used, so the product's polling behaviour cannot be seen or tuned in one place.
- **Fix:** Centralize in `lib/config/polling.ts` and `lib/library/library-constants.ts` with names describing the product behaviour. The library page size should disappear entirely once F3 adds pagination.

### F26. The shared test render helper exists but no test uses it

- **Where:** `test/render.tsx:7-15`, versus duplicated wrappers in `components/sell/asset-processing-status-panel.test.tsx:86-88`, `components/sell/listing-copilot-panel.test.tsx:134-136`, and 13+ other files
- **Severity:** Medium
- **Evidence:** `renderWithQueryClient` is defined and imported by nothing; 15+ test files hand-roll `QueryClientProvider` plus `createTestQueryClient()` instead, and none of them compose an auth wrapper. Same shape as F18 — a shared abstraction that exists, is maintained, and is unused while the duplication it targets keeps growing.
- **Fix:** Extend `test/render.tsx` with `renderWithProviders({ authUser })` and migrate the existing tests. Do this before writing the F5 tests, so the new tests do not add 6 more copies of the wrapper.

### F27. Coverage thresholds exclude all UI code

- **Where:** `vitest.config.ts:31-47`
- **Severity:** Medium
- **Evidence:** The coverage `include` list names only `lib/http/*`, `lib/query/*`, and similar — no `components/**` or `hooks/**`. So the gated number cannot move when UI coverage drops, and F5's untested areas are invisible to CI.
- **Fix:** Either document that UI coverage is intentionally ungated (a legitimate choice), or add a second bucket for the critical UI modules — auth, checkout, library — with a threshold set to today's actual number and ratcheted up. Pairs with [G11](#g11-coverage-is-measured-and-reported-but-never-gated).

### F28. End-to-end coverage is a single smoke spec

- **Where:** `e2e/smoke.spec.ts:1-40`, `package.json:18`
- **Severity:** Medium
- **Evidence:** One stubbed smoke flow. Playwright is installed, configured, and wired into CI — the infrastructure is entirely in place and exercises one path.
- **Fix:** Add authenticated library view and sell dashboard tab navigation using the existing stub-API pattern. Two specs roughly double the value of the Playwright setup you already pay for in CI time ([G12](#g12-frontend-ci-ordering-wastes-feedback-time-and-playwright-shares-the-job)).

### F29. No internationalization layer

- **Where:** repo-wide; representative: `components/site-header.tsx:71-101`
- **Severity:** Low
- **Evidence:** All user-facing copy is inline English with no translation infrastructure. Notably the audit and this project's own docs are bilingual, so the author works across languages even though the product does not.
- **Fix:** For a single-locale pet project, skip it — retrofitting is the standard advice only because it is painful, and paying that cost now for a locale you may never add is not obviously right. If a second locale is ever actually planned, adopt `next-intl` at that moment and before the string count grows further.

---

## G. Build, CI & repository hygiene

### G1. Cursor indexing rules for local secret files never match, and sit in the wrong file

- **Where:** `asblock-backend/.cursorignore:2-5`
- **Severity:** High
- **Evidence:** Every pattern has a stray leading dot — `.appsettings.Development.json`, `.appsettings.Production.json`, `.appsettings.Staging.json`, `.appsettings.Local.json`. The real files are `appsettings.Development.json` etc., so none of these patterns match anything. `asblock-backend/.gitignore:365` correctly ignores `**/appsettings.Development.json` from git, which means the file exists on disk with local secrets (connection strings, JWT key, Stripe keys, encryption key) and is currently indexed. Separately, `.cursorignore` is only read from the **workspace root**, so this file is inert regardless of pattern correctness.
- **Fix:** Create a root `d:\Programming\asblock\.cursorignore` containing `**/appsettings.Development.json`, `**/appsettings.*.local.json`, `**/.env`, `**/.env.local`, `**/dataprotection-keys/`, and delete `asblock-backend/.cursorignore`. Verify with a Cursor search for a string you know is only in `appsettings.Development.json`.

### G2. Restores are not reproducible — no NuGet lock files, and CI caches on a file that does not exist

- **Where:** `.github/workflows/backend-ci.yml:31`, `.github/workflows/dependency-ci.yml:48`; zero `packages.lock.json` files exist anywhere in `asblock-backend/`
- **Severity:** High
- **Evidence:** Both cache keys hash `asblock-backend/**/packages.lock.json`, but no such file exists, so that term contributes nothing. Without `RestorePackagesWithLockFile`, a floating dependency (e.g. `Microsoft.AspNetCore.Authentication.JwtBearer 10.0.5` resolving different transitives) can change the closure between CI runs and your machine. This directly contradicts the roadmap item "Reproducibility та dependency audit" (`assetblock.md:26-31`), which is already marked done.
- **Fix:** Set `<RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>` in a new `Directory.Build.props` (see G3), commit the generated `packages.lock.json` per project, and add `--locked-mode` to the `dotnet restore` step in both workflows so CI fails on drift instead of silently resolving.

### G3. No `Directory.Build.props` — shared MSBuild properties are copy-pasted across 9 projects, and analyzers are off

- **Where:** `asblock-backend/AssetBlock.WebApi/AssetBlock.WebApi.csproj:3-8`, `asblock-backend/AssetBlock.Domain/AssetBlock.Domain.csproj:3-7`, and every other `.csproj`
- **Severity:** High
- **Evidence:** `TargetFramework`, `Nullable`, and `ImplicitUsings` are repeated verbatim in each project file. More importantly, nothing sets `TreatWarningsAsErrors`, `EnableNETAnalyzers`, `AnalysisLevel`, or `EnforceCodeStyleInBuild`. The `.editorconfig` is 450+ lines and encodes real rules (`dotnet_style_readonly_field = true:warning`), but with `EnforceCodeStyleInBuild` unset those `IDE*` rules never run during `dotnet build` — they are IDE-only suggestions. The house style in `asblock-backend/AGENTS.md:68-75` is therefore enforced by reviewer attention alone.
- **Fix:** Add `asblock-backend/Directory.Build.props` with `TargetFramework`, `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors=true`, `EnableNETAnalyzers=true`, `AnalysisLevel=latest-recommended`, `EnforceCodeStyleInBuild=true`, and `RestorePackagesWithLockFile=true`; strip those properties from the individual `.csproj` files. Expect an initial batch of warnings — fix or explicitly suppress each one, then the gate is free forever.

### G4. Dependabot ignores npm entirely, so the dependency CI gate has no fix path

- **Where:** `.github/dependabot.yml:3-21`
- **Severity:** High
- **Evidence:** Only `nuget` (`/asblock-backend`) and `github-actions` (`/`) ecosystems are configured. `asblock-frontend/package.json` has ~60 runtime and ~24 dev dependencies and is never updated automatically; the root `package.json` (husky, lint-staged) likewise. Meanwhile `dependency-ci.yml:68` runs `pnpm deps:check`, which fails the build on a new High/Critical npm advisory — so a transitive npm CVE breaks `main` with nothing proposing a bump.
- **Fix:** Add two `package-ecosystem: "npm"` entries for `/asblock-frontend` and `/`, weekly, with `groups` to batch Radix/`@types`/ESLint bumps into single PRs so review noise stays manageable.

### G5. Pre-commit hooks cover the frontend only — backend commits get no local checks

- **Where:** `.husky/pre-commit:2`, `lint-staged.config.mjs:28-41`
- **Severity:** Medium
- **Evidence:** Both glob patterns are prefixed with `asblock-frontend/`, so a commit touching only `.cs` files runs no formatter, no analyzer, and no test. `README.md:90` documents this as intentional ("Backend-only commits skip the hook work"), but the consequence is that all backend style enforcement is deferred to human review.
- **Fix:** Add a `asblock-backend/**/*.cs` entry to `lint-staged.config.mjs` running `dotnet format --include <files> --no-restore`. Scoped to staged files this stays fast, and it pairs naturally with G3.

### G6. No formatting or style gate in backend CI

- **Where:** `.github/workflows/backend-ci.yml:40-42`
- **Severity:** Medium
- **Evidence:** The pipeline is restore → build → test. There is no `dotnet format --verify-no-changes`, so a PR can drift from the committed `.editorconfig` and still go green. Compare with `frontend-ci.yml:59-61`, which does gate on `pnpm run check` (typecheck + lint + format).
- **Fix:** Add a `dotnet format asblock-backend.slnx --verify-no-changes --no-restore` step after Restore. Together with G3 this brings the backend to parity with the frontend gate.

### G7. Third-party GitHub Actions are unpinned while one job holds `pull-requests: write`

- **Where:** `.github/workflows/backend-ci.yml:10`, `:114`, `:123`; `.github/workflows/secret-scan.yml:25`
- **Severity:** Medium
- **Evidence:** `backend-ci.yml` grants `pull-requests: write` at workflow level and then runs two third-party actions by mutable tag: `irongut/CodeCoverageSummary@v1.3.0` and `marocchino/sticky-pull-request-comment@v3`. `secret-scan.yml` runs `gitleaks/gitleaks-action@v3` with `GITHUB_TOKEN`. A tag can be repointed by the action owner or by an account compromise, and the coverage-comment job would then execute attacker code with a PR-write token.
- **Fix:** Pin all third-party actions to full commit SHAs with a trailing `# vX.Y.Z` comment (Dependabot's `github-actions` ecosystem already updates SHA pins). Additionally move `pull-requests: write` off the workflow and onto only the comment step's job, so build and test run with `contents: read` alone.

### G8. `docs/` is gitignored in the backend, which blocks committing any design docs or ADRs

- **Where:** `asblock-backend/.gitignore:445`
- **Severity:** Medium
- **Evidence:** A bare `docs/` entry. Because it has no leading slash it matches `docs/` at any depth under `asblock-backend/`. There is currently no `docs/` directory and no ADR folder anywhere in the repo — the only "docs" is a frontend page at `asblock-frontend/app/docs/page.tsx`. So the one obvious home for architecture decisions is silently ignored.
- **Fix:** Delete line 445. The project has made several non-obvious, hard-won decisions that live only in `assetblock.md` prose or in reviewer memory — SeaweedFS over MinIO, custom mediator over MediatR, AES-GCM chunk framing, webhook-as-payment-truth, the BFF trust boundary for analytics. Each deserves a short ADR under `asblock-backend/docs/adr/`; they also pay for themselves as agent context.

### G9. No static analysis / SAST workflow

- **Where:** `.github/workflows/` (4 workflows: backend, frontend, dependency, secret-scan)
- **Severity:** Medium
- **Evidence:** CI covers build, test, dependency licenses/vulns, and committed secrets. Nothing scans the code itself for injection, deserialization, path traversal, weak crypto, or authorization patterns. For a repo whose threat surface is "encrypted paid assets + Stripe + JWT", that is the missing quadrant.
- **Fix:** Add `.github/workflows/codeql.yml` using `github/codeql-action` with the `csharp` and `javascript-typescript` languages on PR and a weekly schedule. CodeQL is free for public repositories and needs no configuration for these languages.

### G10. Docker images are pinned by mutable tag, not digest

- **Where:** `asblock-backend/docker-compose.yml:33`, `:54`, `:73`, `:87`, `:96`, `:106`
- **Severity:** Low
- **Evidence:** `chrislusf/seaweedfs:4.42`, `minio/minio:RELEASE.2025-09-07T16-13-09Z`, `valkey/valkey:9.1.1`, `axllent/mailpit:v1.30.0`, `mcr.microsoft.com/dotnet/aspire-dashboard:9.0.0`, `clamav/clamav:1.4.6`. Tags are already a deliberate improvement over the floating `redis:7-alpine` the roadmap removed (`assetblock.md:11`), but tags remain mutable, so `docker compose pull` can silently change the local stack — including the database of a storage provider whose encrypted objects cannot be migrated (`docker-compose.yml:10`).
- **Fix:** Append `@sha256:...` digests to each image. This finishes the reproducibility goal the roadmap already claims for dependencies, and costs one `docker buildx imagetools inspect` per image.

### G11. Coverage is measured and reported but never gated

- **Where:** `.github/workflows/backend-ci.yml:112-126`, `.github/workflows/frontend-ci.yml:49-57`
- **Severity:** Low
- **Evidence:** Backend merges Cobertura reports and posts a sticky PR comment; frontend uploads Vitest coverage as an artifact. Neither fails on a drop. `irongut/CodeCoverageSummary@v1.3.0` supports `thresholds` and `fail`, which are not used.
- **Fix:** Set `thresholds: '55 70'` and `fail: true` on the backend summary step, starting from whatever the current number actually is rather than an aspirational one, and ratchet upward. Do the same with `coverage.thresholds` in `asblock-frontend/vitest.config.ts`. The point is preventing regression, not hitting a number.

### G12. Frontend CI ordering wastes feedback time, and Playwright shares the job

- **Where:** `.github/workflows/frontend-ci.yml:47-80`
- **Severity:** Low
- **Evidence:** Order is tests → check (typecheck/lint/format) → build → Playwright install → e2e, all in one `check` job. A type error is discovered only after the full Vitest suite, and a Playwright browser download (~150 MB, uncached) runs on every push even when the unit tests already failed the build.
- **Fix:** Run `pnpm run typecheck` first as a fast fail, then tests, then build. Split e2e into a second job with `needs: check` and cache `~/.cache/ms-playwright` keyed on the `@playwright/test` version.

### G13. Inconsistent action major versions across workflows

- **Where:** `.github/workflows/frontend-ci.yml:35` versus `.github/workflows/backend-ci.yml:28` and `.github/workflows/dependency-ci.yml:38,45`
- **Severity:** Nit
- **Evidence:** `actions/cache@v4` in frontend CI, `actions/cache@v6` in the other two. Same action, same purpose, two majors.
- **Fix:** Standardize on `v6` everywhere. The `github-actions` Dependabot ecosystem will keep them aligned once they match.

### G14. Missing repository governance files

- **Where:** repository root and `.github/` (no `SECURITY.md`, `CONTRIBUTING.md`, `CODEOWNERS`, `PULL_REQUEST_TEMPLATE.md`, `CHANGELOG.md`)
- **Severity:** Low
- **Evidence:** `LICENSE` (Apache-2.0), `DEPENDENCY-POLICY.md`, and `THIRD-PARTY-NOTICES.md` exist; the process-facing files do not. `README.md:75-77` compresses all of "Contributing / quality" into three sentences.
- **Fix:** Add `SECURITY.md` (how to report a vulnerability in a public repo handling encrypted paid assets — this is the one that actually matters), and a short `.github/PULL_REQUEST_TEMPLATE.md` whose checklist mirrors the "Verify proportionally" section of `.agents/skills/implement-change/SKILL.md`, so human and agent PRs are held to the same bar. Skip `CODEOWNERS` and `CHANGELOG.md` while this is a single-author project — they would be ceremony.

### G15. `README.md` has drifted from the code in two places

- **Where:** `README.md:61`, `README.md:13`
- **Severity:** Low
- **Evidence:** Line 61 says engagement telemetry is "separate from **Vercel Analytics** (deployment metrics only)", but `@vercel/analytics` was deliberately removed (`assetblock.md:12`) and does not appear in `asblock-frontend/package.json`. Line 13 describes `docker-compose.yml` as providing PostgreSQL, but the `postgres` service is commented out at `docker-compose.yml:14-30`. Stale docs are worse than absent docs when agents read them as ground truth.
- **Fix:** Drop the Vercel clause from line 61 and reword line 13 to "PostgreSQL (commented out — run locally or uncomment)". Then add a "docs freshness" line to the implement-change skill (see H4).

### G16. `Microsoft.CodeAnalysis.Analyzers` is referenced by an application project

- **Where:** `asblock-backend/AssetBlock.WebApi/AssetBlock.WebApi.csproj:14-17`
- **Severity:** Nit
- **Evidence:** That package ships analyzers for people *writing* Roslyn analyzers; it adds nothing to an ASP.NET Core host. `asblock-backend/AGENTS.md:75` says to "remove unused direct references".
- **Fix:** Remove the reference and rebuild. If the intent was general code analysis, `EnableNETAnalyzers` in G3 is the correct mechanism.

---

## H. Agent workflow

Assessed independently of any specific model: the question is whether the setup would still produce good changes if the underlying model were swapped or regressed.

The foundation here is unusually strong — nested `AGENTS.md` routing, three purpose-built agents, three shared skills, an explicit review-execution boundary, and `readonly: true` on both reviewers. The findings below are about the gap between *stated* policy and *enforced* policy.

### H1. The highest-risk rules are prose-only; there are no hooks to enforce them

- **Where:** no `.cursor/hooks.json` exists; the rules in question are `asblock-backend/AGENTS.md:47`, `:61`, `:79`, `asblock-frontend/AGENTS.md:60`
- **Severity:** High
- **Evidence:** The rules an agent must never break are stated as sentences: "Never hand-edit migration files, designer files, or the model snapshot" (`asblock-backend/AGENTS.md:47`), "Never commit, log, return, or copy secrets, JWTs, refresh tokens... encryption keys" (`:61`), "do not add Moq" (`:79`), "Do not use `any`, broad casts, disabled lint rules, `@ts-ignore`" (`asblock-frontend/AGENTS.md:60`). Every one is a probabilistic request. Compliance depends on whether the model happened to keep that line in context — which is exactly the dependency you asked to remove.
- **Fix:** Add `.cursor/hooks.json` with a `beforeShellExecution`/`beforeReadFile`-style guard set, or at minimum a `beforeSubmit` script, that hard-blocks: writes to `asblock-backend/AssetBlock.Infrastructure/Migrations/**` and `ApplicationDbContextModelSnapshot.cs`; new occurrences of `Moq`, `@ts-ignore`, `eslint-disable`, `: any`; and any diff adding a line matching a secret-shaped pattern outside `.example` files. A deterministic 40-line script converts four of your most important invariants from "usually respected" to "cannot be violated". This is the single highest-leverage workflow change available.

### H2. Guides describe patterns in prose but never point at a canonical implementation

- **Where:** `asblock-backend/AGENTS.md:30-38`, `.agents/skills/implement-change/SKILL.md:11-13`
- **Severity:** High
- **Evidence:** The backend guide says "Use sealed command/query records, internal sealed handlers, and co-located `*Validator` classes. Follow neighboring use-case structure." That instruction requires the agent to first *find* a good neighbour and then *infer* the pattern — two chances to pick a weak example, and the answer varies by which files happened to be retrieved. There are 100+ use-case folders of differing maturity, so "neighbouring" is not a well-defined target.
- **Fix:** Nominate explicit golden references in `asblock-backend/AGENTS.md` — for example "when adding a command, mirror `AssetBlock.Application/UseCases/Assets/PublishAssetVersion/` exactly (command record, handler, validator, error codes, tests)" and one named read-side query folder. Do the same in `asblock-frontend/AGENTS.md` for a feature slice (page + `*-api.ts` + `*-query.ts` + schema + component + test) and for a BFF route. Concrete exemplars transfer structure far more reliably than adjectives, and they degrade gracefully on weaker models.

### H3. No guardrail or dedicated lane for the single riskiest operation: EF migrations

- **Where:** `asblock-backend/AGENTS.md:47`, `.agents/skills/implement-change/SKILL.md:20`; `.cursor/agents/` contains only `implementer`, `backend-reviewer`, `frontend-reviewer`
- **Severity:** Medium
- **Evidence:** Schema changes are the one class of agent error that is expensive to reverse, and the process is governed entirely by two prose sentences ("Generate migrations only via `dotnet ef migrations add ...` after explicit user approval", "Do not hand-edit EF migrations"). There is no checklist for the parts that actually go wrong: whether the migration is destructive, whether it needs a data backfill, whether the down-migration works, whether new indexes are concurrent, whether the model snapshot diff is limited to the intended entity.
- **Fix:** Add `.agents/skills/add-migration/SKILL.md` with the full sequence (confirm approval → change configuration → `dotnet ef migrations add` → inspect the generated `Up`/`Down` and snapshot diff without editing → run `MigrationSmokePostgresTests` → report the destructive-change assessment). Reference it from `asblock-backend/AGENTS.md:47`. Pair it with the H1 hook so hand-edits are blocked rather than discouraged.

### H4. Guides can drift from code with nothing detecting it

- **Where:** `asblock-frontend/AGENTS.md:56` versus `asblock-frontend/next.config.mjs:8-18`
- **Severity:** Medium
- **Evidence:** The frontend guide instructs agents "Do not alter `images.unoptimized`, theme forcing, analytics, or global styling without an explicit product/deployment reason." `next.config.mjs` has no `images` block at all, and analytics was removed from the project. The rule references configuration that no longer exists. This is the same class of problem as G15 but more damaging: an agent treats `AGENTS.md` as authoritative, so a stale rule actively teaches it a false fact about the codebase, and it will spend context reasoning about a key that isn't there.
- **Fix:** Two things. Correct that line now. Then add a "Deliver" bullet to `.agents/skills/implement-change/SKILL.md`: when a change makes a statement in `AGENTS.md`, `README.md`, or a skill file untrue, update it in the same diff. Guide accuracy is only maintainable if it is part of the definition of done.

### H5. Agent definitions hardcode a model version, which will rot

- **Where:** `.cursor/agents/backend-reviewer.md:8`, `.cursor/agents/frontend-reviewer.md:8`, `.cursor/agents/implementer.md:7`
- **Severity:** Medium
- **Evidence:** All three pin `model: composer-2.5[fast=false]`, and `implementer.md:6` additionally hardcodes the same fact into prose ("Uses Composer 2.5 standard (non-fast)"). When that slug is retired or superseded, three files break or silently fall back, and the prose line is wrong the moment the frontmatter changes.
- **Fix:** Remove the prose duplication in `implementer.md:6` immediately — a fact stated twice will disagree eventually. For the frontmatter, keep an explicit pin only where model choice is load-bearing, and add a one-line comment saying *why* that model (cost? long-context reviews? tool-calling reliability?). Where it isn't load-bearing, drop the key and inherit, so the workflow tracks improvements automatically instead of being frozen at a 2025-era model.

### H6. Reviewers and implementer share one model, so they share blind spots

- **Where:** `.cursor/agents/implementer.md:7` and `.cursor/agents/backend-reviewer.md:8` / `frontend-reviewer.md:8` all specify `composer-2.5[fast=false]`
- **Severity:** Medium
- **Evidence:** The review lane exists to catch what the implementer missed, but both run the same model with the same training-derived habits. If a model has a systematic weakness — say, consistently forgetting `AsNoTracking()` or a deterministic tie-breaker in paged queries — it will neither introduce nor notice it. The review step then produces a false sense of coverage, which is worse than no review because it justifies skipping human attention.
- **Fix:** Point the reviewers at a *different* model family from the implementer, whichever two you have available. Independence of failure modes matters more than the absolute capability of either one, and it's a one-word change per file. This is the model-independent reason your two-lane review design is sound — currently the design is right but the instantiation collapses it back to one opinion.

### H7. `implementer` runs in the background with no automated verification lane

- **Where:** `.cursor/agents/implementer.md:8`, `.agents/skills/implement-change/SKILL.md:22-27`
- **Severity:** Medium
- **Evidence:** `is_background: true` means implementation work completes without a human watching it happen. Verification is requested in prose ("Start with the narrowest affected tests", "Never present skipped verification as passing") but nothing checks that it occurred — the only artifact is the agent's own claim, in its own summary, about commands it says it ran. Root `AGENTS.md:30` asks for "verification commands/results", which is precisely the field a model under pressure to look finished will fabricate.
- **Fix:** Require the implementer to end with a fenced block containing the literal command and its exit code, and make the reviewers treat a missing or unparseable block as an automatic finding — `.agents/skills/review-change/SKILL.md:71-73` already has a `Validation` slot for this, so wire it in as a required input rather than an optional courtesy. Longer term this is also what makes background agents safe to trust.

### H8. No skill for the most frequently repeated task in this codebase

- **Where:** `.agents/skills/` contains only `implement-change`, `review-change`, `handoff-task`
- **Severity:** Low
- **Evidence:** The three existing skills cover generic modes of work. The recurring *concrete* task in this repo is "add one vertical slice": a use-case folder, `ERR_*` code plus `ErrorCodesToErrorMessages` entry, store method, controller action in `ApiRoutes`, BFF route, feature `*-api.ts`/`*-query.ts`, Zod schema, UI, and tests at three levels. That is roughly a dozen coordinated touchpoints, and every one of them is a place where an agent can produce something that compiles but breaks a convention.
- **Fix:** Add `.agents/skills/add-feature-slice/SKILL.md` enumerating those touchpoints as a checklist with the golden references from H2. This is the highest-frequency, highest-drift path in the project, and a checklist is exactly the artifact that makes it model-independent.

### H9. No `.cursor/commands/` for recurring prompts

- **Where:** `.cursor/` contains only `agents/`
- **Severity:** Nit
- **Evidence:** Recurring requests — this audit, a dependency-governance refresh, a pre-PR self-review, a handoff — are retyped from memory each time, so their scope varies run to run and results aren't comparable.
- **Fix:** Add `.cursor/commands/audit.md`, `deps-refresh.md`, and `pre-pr.md` as thin wrappers over the existing skills. Cheap, and it makes repeated runs diffable against each other.

### H10. Claude Code has no permission configuration matching Cursor's guardrails

- **Where:** root `CLAUDE.md` (2 lines, `@AGENTS.md`); `.claude/` is empty
- **Severity:** Nit
- **Evidence:** `CLAUDE.md` correctly delegates instructions to `AGENTS.md`, so *guidance* is shared. But Cursor's `readonly: true` reviewer enforcement has no Claude Code counterpart, and there is no `.claude/settings.json` deny-list. The same repository therefore has meaningfully different safety properties depending on which client is driving.
- **Fix:** Add `.claude/settings.json` with a `permissions.deny` list covering the H1 paths (migrations, snapshot, `appsettings.Development.json`, `.env`). Only worth doing if you actually use Claude Code on this repo; if not, delete the empty `.claude/` directory and keep `CLAUDE.md`.

---

## I. Suggested order of work

There are 197 findings here (38 High, 107 Medium, 43 Low, 9 Nit — no Criticals). Working through them top-to-bottom would be the wrong approach, because some fixes make others unnecessary and one pair is actively unsafe to do in the wrong order. What follows is a sequence, not a ranking.

### Order-dependent pairs — get these right

Three cases where sequence actually matters:

1. **[E2](#e2-concurrent-refresh-has-no-single-flight-lock) before [C3](#c3-no-refresh-token-reuse-detection).** Reuse detection revokes every session when a rotated token is replayed. Without the single-flight lock, ordinary parallel page loads replay rotated tokens routinely — so shipping C3 first turns a latency bug into users being logged out constantly.
2. **[E9](#e9-route-handler-boilerplate-is-duplicated-across-55-files) before E1, E3, E5, E6, E7, E11, E20.** Those seven are all instances of one handler diverging from 55 near-identical copies. Fix them individually and you fix seven symptoms; add the wrapper first and they collapse into one change that also prevents the next occurrence.
3. **[G3](#g3-no-directorybuildprops--shared-msbuild-properties-are-copy-pasted-across-9-projects-and-analyzers-are-off) before the backend cleanup findings.** Turning on analyzers and `TreatWarningsAsErrors` will surface a batch of warnings that overlap with findings in A and B. Do it first and you fix each thing once, with the compiler telling you when you are done.

Similarly, [F26](#f26-the-shared-test-render-helper-exists-but-no-test-uses-it) (test harness) belongs before [F5](#f5-major-feature-areas-have-no-component-tests) (write the missing tests), and [A2](#a2-upload-and-publish-duplicate-the-entire-encrypt-and-upload-pipeline) (deduplicate the encrypt pipeline) belongs before [A3](#a3-taskrun-in-the-encrypt-pipeline-discards-the-cancellation-token), [D17](#d17-decrypt-allocates-two-heap-buffers-per-chunk), and [D18](#d18-a-fresh-aesgcm-instance-is-constructed-per-chunk), so those get fixed in one place instead of two.

### Stage 1 — small fixes with disproportionate value

Each of these is roughly a one-line to one-hour change:

- [C6](#c6-the-global-exception-handler-returns-500-without-logging-the-exception) — log the exception. Right now every unexpected 500 in production is invisible. Nothing else on this list improves your ability to diagnose problems as much per line changed.
- [C2](#c2-there-is-no-logout-endpoint-so-logging-out-does-not-invalidate-the-refresh-token) — wire up logout. The revocation service already exists; this is plumbing, and it is the clearest real security gap.
- [B1](#b1-review-average-is-computed-in-memory-over-every-rating-row) and [B2](#b2-mark-all-read-loads-every-unread-notification-and-updates-them-one-by-one) — one `AverageAsync`, one `ExecuteUpdateAsync`. Two lines, two O(n)-to-O(1) improvements.
- [G1](#g1-cursor-indexing-rules-for-local-secret-files-never-match-and-sit-in-the-wrong-file) — your local secrets are currently being indexed. The existing `.cursorignore` is inert both because the patterns have stray leading dots and because it is in the wrong directory.
- [C5](#c5-avatar-url-accepts-any-scheme-unlike-social-links) — apply the URL rule you already wrote for social links.
- [F4](#f4-the-user-profile-is-fetched-twice-per-request) — wrap one function in `cache()`.
- [E4](#e4-failed-refresh-leaves-stale-auth-cookies-in-the-generic-bff-path), [E8](#e8-fetchbackend-accepts-absolute-urls-in-path), [C19](#c19-jwt-validation-does-not-pin-the-signing-algorithm) — one guard each.

### Stage 2 — correctness and data integrity

Bugs that produce wrong behaviour rather than slow behaviour:

- [D1](#d1-outbox-side-effects-can-repeat-because-handlers-are-not-idempotent) and [D2](#d2-outbox-rows-are-stuck-forever-after-ten-attempts-with-no-dead-letter-state) — duplicate emails, and permanently-failed side effects silently dropped. The `AddYears(100)` pseudo-dead-letter should not survive.
- [E2](#e2-concurrent-refresh-has-no-single-flight-lock), then [C3](#c3-no-refresh-token-reuse-detection) — in that order.
- [B8](#b8-collection-item-insert-computes-position-without-locking-the-collection) and [B7](#b7-bundle-asset-locking-issues-one-select--for-update-per-asset) — a live race producing constraint violations, and a lock-ordering deadlock.
- [D7](#d7-job-leases-are-never-renewed-although-renewlease-exists) — `RenewLease` exists and is never called, so a slow handler means two workers process one job.
- [F2](#f2-private-cache-clearing-misses-the-analytics-and-admin-namespaces) — the previous user's revenue figures and admin audit data survive logout in the query cache.
- [F3](#f3-the-library-page-is-silently-capped-at-100-purchases) — users cannot see purchases past the hundredth.
- [A1](#a1-stripe-webhook-throws-raw-exceptions-instead-of-returning-a-result) — Stripe retries a permanently unprocessable event forever.
- [B13](#b13-no-global-soft-delete-query-filter-getbyid-returns-deleted-assets) — make the soft-delete invariant structural instead of per-query discipline.

### Stage 3 — the structural refactors

Larger, and the ones that stop findings from recurring:

- [E9](#e9-route-handler-boilerplate-is-duplicated-across-55-files) — the BFF route wrapper, which subsumes seven findings.
- [A2](#a2-upload-and-publish-duplicate-the-entire-encrypt-and-upload-pipeline) — one encrypt-and-upload service instead of two copies of the most security-sensitive code in the project.
- [C11](#c11-the-getuserid-null-check-is-copy-pasted-57-times) — `ICurrentUser`, removing ~285 lines and the `!` at `ReviewsController.cs:28`.
- [G3](#g3-no-directorybuildprops--shared-msbuild-properties-are-copy-pasted-across-9-projects-and-analyzers-are-off) + [G2](#g2-restores-are-not-reproducible--no-nuget-lock-files-and-ci-caches-on-a-file-that-does-not-exist) — analyzers on, restores reproducible. This is the one that makes the house style in `AGENTS.md` mechanically enforced instead of reviewer-dependent.
- [D23](#d23-datetimeutcnow-is-used-directly-in-59-production-files-while-timeprovider-is-registered) — adopt the `TimeProvider` that is already registered, which also fixes the flaky sleep-based tests in [D24](#d24-time-dependent-tests-use-real-wall-clock-delays).
- [F6](#f6-four-god-components-carry-400650-lines-each) — split the four large components, starting with `sell-my-collections.tsx` to establish the pattern.
- [B4](#b4-catalog-and-seller-listing-pages-run-a-correlated-avg-subquery-per-row) and [B5](#b5-seller-listing-projection-repeats-the-same-version-subquery-six-times) — the two worst query patterns, on the catalog path.

### Stage 4 — before it gets expensive

Three findings are cheap now and expensive later, purely because of accumulating data:

- [C21](#c21-encryption-wire-format-carries-no-key-identifier) — add a key id to the chunk header. With little stored data this is a small change; once the marketplace holds real encrypted assets, key rotation requires rewriting all of it with no way to track progress.
- [D4](#d4-orphan-cleanup-materializes-the-entire-object-listing) — the storage listing is loaded fully into memory. It works today because there is not much stored.
- [A24](#a24-money-is-a-bare-decimal-with-a-separate-cents-pipeline-for-analytics) — a `UsdAmount` value object, before the double-entry ledger work in `assetblock.md:74` adds a fourth money representation.

### Stage 5 — the agent workflow

Worth doing early despite being last here, because it changes how every subsequent fix gets made:

- [H1](#h1-the-highest-risk-rules-are-prose-only-there-are-no-hooks-to-enforce-them) — hooks that hard-block migration edits, `Moq`, `@ts-ignore`, and secret-shaped diffs. Converts four of your most important rules from "usually respected" to "cannot be violated", which is exactly the model-independence you asked about.
- [H2](#h2-guides-describe-patterns-in-prose-but-never-point-at-a-canonical-implementation) — name golden reference implementations instead of saying "follow neighbouring structure". With 100+ use-case folders of varying maturity, "neighbouring" is not a well-defined target, and [A21](#a21-validators-live-under-two-competing-folder-conventions) and [A22](#a22-new-listing-copilot-validators-are-public-where-peers-are-internal) are what that ambiguity produces.
- [H6](#h6-reviewers-and-implementer-share-one-model-so-they-share-blind-spots) — point the reviewers at a different model family from the implementer. One word per file, and it is what makes the two-lane review design actually deliver independent opinions.
- [H4](#h4-guides-can-drift-from-code-with-nothing-detecting-it) and [G15](#g15-readmemd-has-drifted-from-the-code-in-two-places) — fix the stale guide and README statements, then make doc accuracy part of the definition of done. A stale `AGENTS.md` line is worse than a missing one, because an agent treats it as authoritative and reasons about configuration that no longer exists.

### A note on what not to do

Some findings here are deliberately marked Low or Nit and should probably stay unfixed. [F29](#f29-no-internationalization-layer) (no i18n) is the clearest: the standard advice to "adopt i18n early" exists because retrofitting hurts, but paying that cost now for a locale you may never add is not obviously correct. [E24](#e24-proxyts-role-parsing-is-intentionally-unverified) needs no code change at all — the comment is already accurate. [D26](#d26-minio-and-seaweedfs-adapters-are-identical-delegating-shells) is inert duplication. [G14](#g14-missing-repository-governance-files) recommends `SECURITY.md` but explicitly argues against `CODEOWNERS` and `CHANGELOG.md` while this is single-author.

The volume of findings is a function of how much surface this project covers, not a signal that something is wrong with it. Several things here — the options validators, the dependency governance, the zero `TODO` markers, the review execution boundary, the universal same-origin enforcement across 43 BFF handlers — are better than what most production codebases manage.
