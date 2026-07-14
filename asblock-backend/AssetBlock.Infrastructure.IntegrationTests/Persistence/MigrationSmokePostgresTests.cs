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
}
