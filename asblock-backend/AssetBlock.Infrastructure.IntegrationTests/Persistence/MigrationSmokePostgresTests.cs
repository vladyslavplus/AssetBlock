using AssetBlock.Infrastructure.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence;

[Collection(nameof(PostgresStoreCollection))]
public sealed class MigrationSmokePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldCreateCommerceAndCatalogTables()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var tables = await db.Database.SqlQueryRaw<string>(
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
            "asset_listing_suggestions"
        ]);

        (await db.Assets.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldEnablePgTrgmSearchVectorAndCatalogIndexes()
    {
        await using var db = await fixture.CreateCleanDbContext();

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

        var indexNames = await db.Database.SqlQueryRaw<string>(
                """
                SELECT indexname AS "Value"
                FROM pg_indexes
                WHERE tablename IN ('assets', 'asset_tags', 'reviews')
                """)
            .ToListAsync();

        indexNames.Should().Contain("IX_assets_search_vector");
        indexNames.Should().Contain("IX_assets_Title_trgm");
        indexNames.Should().Contain("IX_assets_Description_trgm");
        indexNames.Should().Contain("IX_assets_catalog_CreatedAt_Id");
        indexNames.Should().Contain("IX_assets_catalog_CategoryId_CreatedAt_Id");
        indexNames.Should().Contain("IX_assets_catalog_AuthorId_CreatedAt_Id");
        indexNames.Should().Contain("IX_asset_tags_TagId_AssetId");
        indexNames.Should().Contain("IX_reviews_AssetId");
    }

    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldCreateAuditLogsSchemaWithoutUserFk()
    {
        await using var db = await fixture.CreateCleanDbContext();

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

        var columnTypes = await db.Database.SqlQueryRaw<string>(
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

        var indexNames = await db.Database.SqlQueryRaw<string>(
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
    public async Task MigrateAsync_WhenFreshDatabase_ShouldHaveNoPendingMigrationsAndContainCommerceInvariantMigration()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var pending = await db.Database.GetPendingMigrationsAsync();
        pending.Should().BeEmpty();

        var applied = await db.Database.GetAppliedMigrationsAsync();
        applied.Should().Contain(m => m.Contains("AddCollectionsBundlesAndOrders", StringComparison.OrdinalIgnoreCase));
        applied.Should().Contain(m =>
            m.Contains("AddCheckoutReconciliationAndCommerceInvariants", StringComparison.OrdinalIgnoreCase));
        applied.Should().Contain(m =>
            m.Contains("AddSellerEngagementAnalytics", StringComparison.OrdinalIgnoreCase));
        applied.Should().Contain(m =>
            m.Contains("AddAssetProcessingJobs", StringComparison.OrdinalIgnoreCase));
        applied.Should().Contain(m =>
            m.Contains("AddAssetVersionProcessingLifecycle", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldCreateAssetVersionProcessingLifecycleAndAnalysisTables()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var tables = await db.Database.SqlQueryRaw<string>(
                """
                SELECT table_name AS "Value"
                FROM information_schema.tables
                WHERE table_schema = 'public'
                  AND table_name = 'asset_archive_analyses'
                """)
            .ToListAsync();

        tables.Should().ContainSingle();

        var versionColumns = await db.Database.SqlQueryRaw<string>(
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

        var versionChecks = await db.Database.SqlQueryRaw<string>(
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

        var analysisChecks = await db.Database.SqlQueryRaw<string>(
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
        await using var db = await fixture.CreateCleanDbContext();

        var tables = await db.Database.SqlQueryRaw<string>(
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

        var checkoutChecks = await db.Database.SqlQueryRaw<string>(
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
    public async Task MigrateUpgrade_WhenLegacyVersionsExist_ShouldBackfillProcessingStatusAndHaveNoPermanentDefault()
    {
        // 1. Drop and recreate schema; apply migrations up to AddAssetProcessingJobs only
        NpgsqlConnection.ClearAllPools();
        await using (var setup = fixture.CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync("""
                DROP SCHEMA IF EXISTS public CASCADE;
                CREATE SCHEMA public;
                """);

            await setup.Database.MigrateAsync("20260824081315_AddAssetProcessingJobs");
        }

        NpgsqlConnection.ClearAllPools();

        // 2. Insert legacy asset + multiple version rows directly (current and non-current, no processing columns yet)
        var legacyAssetId = Guid.NewGuid();
        var legacyV1Id = Guid.NewGuid();
        var legacyV2Id = Guid.NewGuid();
        var legacyCreatedAt1 = DateTimeOffset.UtcNow.AddDays(-30);
        var legacyCreatedAt2 = DateTimeOffset.UtcNow.AddDays(-15);

        await using (var seed = fixture.CreateDbContext())
        {
            // Insert a minimal category and author required by FKs
            var categoryId = Guid.NewGuid();
            var authorId = Guid.NewGuid();

            await seed.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO users ("Id","Username","Email","PasswordHash","Role","CreatedAt")
                VALUES ({0},'legacy','legacy@test.com','test-password-hash','User',{1})
                """,
                authorId, legacyCreatedAt1);

            await seed.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO categories ("Id","Name","Slug","Description","CreatedAt","UpdatedAt")
                VALUES ({0},'Test','test','Test',{1},{2})
                """,
                categoryId, legacyCreatedAt1, legacyCreatedAt1);

            await seed.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO assets ("Id","AuthorId","CategoryId","Title","Description","Price","CreatedAt","UpdatedAt")
                VALUES ({0},{1},{2},'Legacy Asset','Desc',10,{3},{4})
                """,
                legacyAssetId, authorId, categoryId, legacyCreatedAt1, legacyCreatedAt1);

            // Legacy non-current version
            await seed.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO asset_versions ("Id","AssetId","VersionNumber","IsCurrent","StorageKey","FileName","ContentLength","ContentSha256","ReleaseNotes","LicenseCode","LicenseTemplateVersion","LicenseDisplayName","LicenseTerms","CreatedAt")
                VALUES ({0},{1},1,false,'assets/test/v1.zip','v1.zip',100,'abc123','Initial release','MIT','1.0','MIT','Terms',{2})
                """,
                legacyV1Id, legacyAssetId, legacyCreatedAt1);

            // Legacy current version
            await seed.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO asset_versions ("Id","AssetId","VersionNumber","IsCurrent","StorageKey","FileName","ContentLength","ContentSha256","ReleaseNotes","LicenseCode","LicenseTemplateVersion","LicenseDisplayName","LicenseTerms","CreatedAt")
                VALUES ({0},{1},2,true,'assets/test/v2.zip','v2.zip',200,'def456','Second release','MIT','1.0','MIT','Terms',{2})
                """,
                legacyV2Id, legacyAssetId, legacyCreatedAt2);
        }

        NpgsqlConnection.ClearAllPools();

        // 3. Apply the remaining migration (AddAssetVersionProcessingLifecycle)
        await using (var upgrade = fixture.CreateDbContext())
        {
            await upgrade.Database.MigrateAsync();
        }

        NpgsqlConnection.ClearAllPools();

        // 4. Verify backfill: both pre-existing versions (current and non-current) should be READY with ProcessingUpdatedAt = CreatedAt
        await using var verify = fixture.CreateDbContext();

        var backfilledRows = await verify.Database.SqlQueryRaw<string>(
            """
            SELECT "ProcessingStatus" AS "Value"
            FROM asset_versions
            WHERE "Id" IN ({0}, {1})
            ORDER BY "VersionNumber"
            """,
            legacyV1Id, legacyV2Id).ToListAsync();

        backfilledRows.Should().HaveCount(2);
        backfilledRows.Should().OnlyContain(s => s == "READY");

        var updatedAtRows = await verify.Database.SqlQueryRaw<bool>(
            """
            SELECT ("ProcessingUpdatedAt" = "CreatedAt") AS "Value"
            FROM asset_versions
            WHERE "Id" IN ({0}, {1})
            """,
            legacyV1Id, legacyV2Id).ToListAsync();

        updatedAtRows.Should().HaveCount(2);
        updatedAtRows.Should().OnlyContain(b => b == true);

        // 5. Verify no permanent column default remains for ProcessingStatus or ProcessingUpdatedAt
        var columnDefaults = await verify.Database.SqlQueryRaw<string>(
            """
            SELECT column_name || ':' || is_nullable || ':' || COALESCE(column_default, 'NULL') AS "Value"
            FROM information_schema.columns
            WHERE table_name = 'asset_versions'
              AND column_name IN ('ProcessingStatus', 'ProcessingUpdatedAt')
            ORDER BY column_name
            """).ToListAsync();

        columnDefaults.Should().Contain([
            "ProcessingStatus:NO:NULL",
            "ProcessingUpdatedAt:NO:NULL"
        ]);

        // 6. Verify regex check constraint rejects invalid error code format
        var actBadCode = () => verify.Database.ExecuteSqlRawAsync(
            """
            UPDATE asset_versions
            SET "ProcessingStatus" = 'REJECTED',
                "ProcessingErrorCode" = 'BAD-CODE',
                "ProcessingErrorSummary" = 'Invalid code with hyphen'
            WHERE "Id" = {0}
            """, legacyV1Id);

        (await actBadCode.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);

        // 7. Verify new insert without processing fields fails closed (NOT NULL violation)
        var newVersionId = Guid.NewGuid();
        var actMissingProcessing = () => verify.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO asset_versions ("Id","AssetId","VersionNumber","IsCurrent","StorageKey","FileName","ContentLength","ContentSha256","ReleaseNotes","LicenseCode","LicenseTemplateVersion","LicenseDisplayName","LicenseTerms","CreatedAt")
            VALUES ({0},{1},3,false,'assets/test/v3.zip','v3.zip',300,'ghi789','Third release','MIT','1.0','MIT','Terms',NOW())
            """,
            newVersionId, legacyAssetId);

        (await actMissingProcessing.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(PostgresErrorCodes.NotNullViolation);
    }
}
