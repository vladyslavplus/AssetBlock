using AssetBlock.Infrastructure.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence;

[Collection(nameof(PostgresStoreCollection))]
public sealed class MigrationSmokePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task MigrateAsync_WhenFreshDatabase_ShouldApplyAllModelMigrationsWithoutPending()
    {
        await using var db = await fixture.CreateCleanDbContext();

        var defined = db.Database.GetMigrations().ToList();
        var applied = (await db.Database.GetAppliedMigrationsAsync()).ToList();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();

        defined.Should().NotBeEmpty();
        applied.Should().BeEquivalentTo(defined, options => options.WithStrictOrdering());
        pending.Should().BeEmpty();

        // Observable proof that the migrated schema is usable.
        var assetCount = await db.Assets.CountAsync();
        assetCount.Should().Be(0);
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
}
