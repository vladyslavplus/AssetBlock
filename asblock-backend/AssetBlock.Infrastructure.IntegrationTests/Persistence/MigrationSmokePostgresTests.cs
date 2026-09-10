using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence;

[Collection(nameof(PostgresStoreCollection))]
public sealed class MigrationSmokePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldCreateCommerceAndCatalogTables()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        List<string> tables = await db.Database.SqlQueryRaw<string>(
                """
                SELECT table_name AS "Value"
                FROM information_schema.tables
                WHERE table_schema = 'public'
                """)
            .ToListAsync();

        tables.Should().Contain([
            "assets",
            "purchases",
            "orders",
            "order_lines",
            "checkout_intents",
            "checkout_reservations",
            "collections",
            "collection_items",
            "bundles",
            "bundle_revisions",
            "bundle_revision_items",
            "asset_listing_suggestions",
            "processed_stripe_webhook_events",
            "asset_embeddings"
        ]);

        (await db.Assets.CountAsync()).Should().Be(0);
        (await db.ProcessedStripeWebhookEvents.CountAsync()).Should().Be(0);
        (await db.AssetEmbeddings.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldEnablePgTrgmSearchVectorAndCatalogIndexes()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        var hasPgTrgm = await db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1 FROM pg_extension WHERE extname = 'pg_trgm'
                ) AS "Value"
                """)
            .SingleAsync();
        hasPgTrgm.Should().BeTrue();

        var hasSearchVector = await db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.columns
                    WHERE table_name = 'assets' AND column_name = 'search_vector'
                ) AS "Value"
                """)
            .SingleAsync();
        hasSearchVector.Should().BeTrue();

        List<string> indexNames = await db.Database.SqlQueryRaw<string>(
                """
                SELECT indexname AS "Value"
                FROM pg_indexes
                WHERE tablename IN (
                    'assets',
                    'asset_tags',
                    'reviews',
                    'purchases',
                    'user_notifications',
                    'bundle_revisions',
                    'collections',
                    'categories',
                    'processed_stripe_webhook_events'
                )
                """)
            .ToListAsync();

        indexNames.Should().Contain("IX_processed_stripe_webhook_events_StripeEventId");
        indexNames.Should().Contain("IX_assets_search_vector");
        indexNames.Should().Contain("IX_assets_Title_trgm");
        indexNames.Should().Contain("IX_assets_Description_trgm");
        indexNames.Should().Contain("IX_assets_catalog_CreatedAt_Id");
        indexNames.Should().Contain("IX_assets_catalog_CategoryId_CreatedAt_Id");
        indexNames.Should().Contain("IX_assets_catalog_AuthorId_CreatedAt_Id");
        indexNames.Should().Contain("IX_asset_tags_TagId_AssetId");
        indexNames.Should().Contain("IX_reviews_AssetId");
        indexNames.Should().Contain("IX_purchases_user_purchased_at_id");
        indexNames.Should().Contain("IX_user_notifications_recipient_unread_created_id");
        indexNames.Should().Contain("IX_bundle_revisions_Title_trgm");
        indexNames.Should().Contain("IX_bundle_revisions_Description_trgm");
        indexNames.Should().Contain("IX_collections_Title_trgm");
        indexNames.Should().Contain("IX_collections_Description_trgm");
        indexNames.Should().Contain("IX_categories_Name_trgm");
        indexNames.Should().Contain("IX_categories_Slug_trgm");
        indexNames.Should().Contain("IX_categories_Description_trgm");
    }

    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldCreateAuditLogsSchemaWithoutUserFk()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        var hasTable = await db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.tables
                    WHERE table_schema = 'public' AND table_name = 'audit_logs'
                ) AS "Value"
                """)
            .SingleAsync();
        hasTable.Should().BeTrue();

        List<string> columnTypes = await db.Database.SqlQueryRaw<string>(
                """
                SELECT column_name || ':' || data_type AS "Value"
                FROM information_schema.columns
                WHERE table_name = 'audit_logs'
                """)
            .ToListAsync();

        columnTypes.Should().Contain(c => c.StartsWith("Id:", StringComparison.Ordinal));
        columnTypes.Should().Contain("MetadataJson:jsonb");

        var hasUserFk = await db.Database.SqlQueryRaw<bool>(
                """
                SELECT EXISTS (
                    SELECT 1
                    FROM information_schema.table_constraints tc
                    JOIN information_schema.key_column_usage kcu
                      ON tc.constraint_name = kcu.constraint_name
                     AND tc.table_schema = kcu.table_schema
                    WHERE tc.table_name = 'audit_logs'
                      AND tc.constraint_type = 'FOREIGN KEY'
                      AND kcu.column_name = 'ActorUserId'
                ) AS "Value"
                """)
            .SingleAsync();
        hasUserFk.Should().BeFalse();

        List<string> indexNames = await db.Database.SqlQueryRaw<string>(
                """
                SELECT indexname AS "Value"
                FROM pg_indexes
                WHERE tablename = 'audit_logs'
                """)
            .ToListAsync();

        indexNames.Should().Contain("IX_audit_logs_OccurredAt_Id");
        indexNames.Should().Contain("IX_audit_logs_ActorUserId_OccurredAt_Id");
        indexNames.Should().Contain("IX_audit_logs_Action_OccurredAt_Id");
        indexNames.Should().Contain("IX_audit_logs_Outcome_OccurredAt_Id");
        indexNames.Should().Contain("IX_audit_logs_ResourceType_ResourceId_OccurredAt_Id");
    }

    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldHaveNoPendingMigrationsAndContainInitialCreate()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        IEnumerable<string> pending = await db.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty();

        IEnumerable<string> applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().ContainSingle(m => m.Contains("InitialCreate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldCreateAssetVersionProcessingLifecycleAndAnalysisTables()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        List<string> tables = await db.Database.SqlQueryRaw<string>(
                """
                SELECT table_name AS "Value"
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = 'asset_archive_analyses'
                """)
            .ToListAsync();

        tables.Should().ContainSingle();

        List<string> versionColumns = await db.Database.SqlQueryRaw<string>(
                """
                SELECT column_name AS "Value"
                FROM information_schema.columns
                WHERE table_name = 'asset_versions'
                  AND column_name IN (
                    'ProcessingStatus',
                    'ProcessingErrorCode',
                    'ProcessingErrorSummary',
                    'ProcessingUpdatedAt'
                  )
                """)
            .ToListAsync();

        versionColumns.Should().HaveCount(4);

        List<string> versionChecks = await db.Database.SqlQueryRaw<string>(
                """
                SELECT conname AS "Value"
                FROM pg_constraint
                WHERE contype = 'c'
                  AND conrelid = 'asset_versions'::regclass
                  AND conname IN (
                    'CK_asset_versions_processing_status',
                    'CK_asset_versions_processing_error_code',
                    'CK_asset_versions_ready_current',
                    'CK_asset_versions_state_error_consistency'
                  )
                """)
            .ToListAsync();

        versionChecks.Should().HaveCount(4);

        List<string> analysisChecks = await db.Database.SqlQueryRaw<string>(
                """
                SELECT conname AS "Value"
                FROM pg_constraint
                WHERE contype = 'c'
                  AND conrelid = 'asset_archive_analyses'::regclass
                  AND conname IN (
                    'CK_asset_archive_analyses_file_count',
                    'CK_asset_archive_analyses_total_expanded_bytes',
                    'CK_asset_archive_analyses_readme_content_size',
                    'CK_asset_archive_analyses_manifest_metadata',
                    'CK_asset_archive_analyses_manifest_metadata_size'
                  )
                """)
            .ToListAsync();

        analysisChecks.Should().HaveCount(5);
    }

    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldCreateEngagementAnalyticsTablesAndCheckoutAttributionChecks()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        List<string> tables = await db.Database.SqlQueryRaw<string>(
                """
                SELECT table_name AS "Value"
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name IN (
                    'analytics_events',
                    'seller_analytics_daily',
                    'product_analytics_daily',
                    'collection_analytics_daily',
                    'traffic_analytics_daily'
                  )
                """)
            .ToListAsync();

        tables.Should().HaveCount(5);

        List<string> checkoutChecks = await db.Database.SqlQueryRaw<string>(
                """
                SELECT conname AS "Value"
                FROM pg_constraint
                WHERE contype = 'c'
                  AND conrelid = 'checkout_intents'::regclass
                  AND conname IN (
                    'CK_checkout_intents_attribution_collection',
                    'CK_checkout_intents_attribution_null_consistency',
                    'CK_checkout_intents_attribution_referrer_host',
                    'CK_checkout_intents_AttributionSource'
                  )
                """)
            .ToListAsync();

        checkoutChecks.Should().HaveCount(4);
    }

    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldEnforceConstraintsAndSupportOutboxAndRatingAggregates()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();

        var hasRefreshTokenIndex = await db.Database.SqlQueryRaw<bool>(
            """
            SELECT EXISTS (
                SELECT 1
                FROM pg_indexes
                WHERE tablename = 'refresh_tokens' AND indexname = 'IX_refresh_tokens_expires_at'
            ) AS "Value"
            """).SingleAsync();
        hasRefreshTokenIndex.Should().BeTrue();

        var authorId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        db.Users.AddRange(
            new Domain.Core.Entities.User { Id = authorId, Username = "author", Email = "author@test.com", PasswordHash = "h", Role = AppRoles.USER, CreatedAt = now },
            new Domain.Core.Entities.User { Id = reviewerId, Username = "rev", Email = "rev@test.com", PasswordHash = "h", Role = AppRoles.USER, CreatedAt = now });

        db.Categories.Add(new Domain.Core.Entities.Category { Id = categoryId, Name = "Tools", Slug = "tools", Description = "Tools", CreatedAt = now, UpdatedAt = now });

        db.Assets.Add(new Domain.Core.Entities.Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = "Fresh Asset",
            Description = "Desc",
            Price = 15m,
            RatingCount = 1,
            RatingAverage = 5.0d,
            CreatedAt = now,
            UpdatedAt = now
        });

        db.AssetVersions.Add(new Domain.Core.Entities.AssetVersion
        {
            Id = versionId,
            AssetId = assetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "assets/test.zip",
            FileName = "test.zip",
            ContentLength = 1024,
            ContentSha256 = "abc",
            ReleaseNotes = "Initial",
            LicenseCode = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL).Code,
            LicenseTemplateVersion = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL).TemplateVersion,
            LicenseDisplayName = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL).DisplayName,
            LicenseTerms = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL).TermsPlainText,
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = now,
            CreatedAt = now
        });

        db.Reviews.Add(new Domain.Core.Entities.Review
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            UserId = reviewerId,
            Rating = 5,
            Comment = "Great!",
            CreatedAt = now
        });

        var outboxMsgId = Guid.NewGuid();
        db.OutboxMessages.Add(new Domain.Core.Entities.OutboxMessage
        {
            Id = outboxMsgId,
            Type = "EmailDispatch",
            Payload = "{}",
            OccurredAt = now,
            Status = Domain.Core.Enums.OutboxMessageStatus.PENDING,
            AttemptCount = 0
        });

        await db.SaveChangesAsync();

        var store = new OutboxStore(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<OutboxStore>.Instance);
        IReadOnlyList<OutboxMessage> batch = await store.ClaimPendingBatch(10, TimeSpan.FromMinutes(5));
        batch.Should().ContainSingle(m => m.Id == outboxMsgId);

        Asset loadedAsset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);
        loadedAsset.RatingCount.Should().Be(1);
        loadedAsset.RatingAverage.Should().Be(5.0d);
    }

    [Fact]
    public async Task CategoryStore_WhenSearchingBySlug_ShouldHandleMixedCaseAndEscapedWildcards()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var c1 = new Domain.Core.Entities.Category { Id = Guid.NewGuid(), Name = "Special 100% Deals", Slug = "special-100%-deal_v1\\pack", Description = "A 100% discount", CreatedAt = DateTimeOffset.UtcNow };
        var c2 = new Domain.Core.Entities.Category { Id = Guid.NewGuid(), Name = "Other Items", Slug = "other-items-deal-v1-pack", Description = "Normal", CreatedAt = DateTimeOffset.UtcNow };
        db.Categories.AddRange(c1, c2);
        await db.SaveChangesAsync();

        var store = new CategoryStore(db, Microsoft.Extensions.Logging.Abstractions.NullLogger<CategoryStore>.Instance);

        // Mixed case search matching slug
        PagedResult<Category> caseMatch = await store.GetPaged(new Domain.Core.Dto.Categories.GetCategoriesRequest { Search = "SPECIAL-100%" });
        caseMatch.Items.Should().ContainSingle();
        caseMatch.Items[0].Id.Should().Be(c1.Id);

        // Literal '%' in slug
        PagedResult<Category> percentMatch = await store.GetPaged(new Domain.Core.Dto.Categories.GetCategoriesRequest { Search = "100%" });
        percentMatch.Items.Should().ContainSingle();
        percentMatch.Items[0].Id.Should().Be(c1.Id);

        // Literal '_' in slug (should not match '-' in other item)
        PagedResult<Category> underscoreMatch = await store.GetPaged(new Domain.Core.Dto.Categories.GetCategoriesRequest { Search = "deal_v1" });
        underscoreMatch.Items.Should().ContainSingle();
        underscoreMatch.Items[0].Id.Should().Be(c1.Id);

        // Literal '\' in slug
        PagedResult<Category> backslashMatch = await store.GetPaged(new Domain.Core.Dto.Categories.GetCategoriesRequest { Search = "v1\\pack" });
        backslashMatch.Items.Should().ContainSingle();
        backslashMatch.Items[0].Id.Should().Be(c1.Id);
    }
}
