using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AssetBlock.Infrastructure.Tests.Persistence.Configurations;

public sealed class AssetListingSuggestionConfigurationTests
{
    [Fact]
    public void Configuration_ShouldSetExactTableConstraintsAndCascadeFk()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new ApplicationDbContext(options);
        IModel model = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType? entityType = model.FindEntityType(typeof(AssetListingSuggestion));
        entityType.Should().NotBeNull();
        entityType.GetTableName().Should().Be("asset_listing_suggestions");

        entityType.FindPrimaryKey()!.Properties.Select(p => p.Name).Should().Equal("JobId");
        entityType.GetCheckConstraints().Select(c => c.Name).Should().Contain([
            "CK_asset_listing_suggestions_provider",
            "CK_asset_listing_suggestions_content_hash",
            AssetListingSuggestionConfiguration.CK_TAGS_TYPE,
            AssetListingSuggestionConfiguration.CK_TAGS_LENGTH,
            AssetListingSuggestionConfiguration.CK_TAGS_ITEMS,
            AssetListingSuggestionConfiguration.CK_TAGS_SIZE,
            "CK_asset_listing_suggestions_input_tokens",
            "CK_asset_listing_suggestions_output_tokens"
        ]);
        entityType.GetCheckConstraints().Single(c => c.Name == "CK_asset_listing_suggestions_provider").Sql
            .Should().Be("\"Provider\" IN ('OPENROUTER', 'OLLAMA')");
        entityType.GetCheckConstraints().Single(c => c.Name == "CK_asset_listing_suggestions_content_hash").Sql
            .Should().Be("\"ContentHash\" ~ '^[a-f0-9]{64}$'");
        entityType.GetCheckConstraints().Single(c => c.Name == AssetListingSuggestionConfiguration.CK_TAGS_TYPE).Sql
            .Should().Be(AssetListingSuggestionConfiguration.SqlTagsType);
        entityType.GetCheckConstraints().Single(c => c.Name == AssetListingSuggestionConfiguration.CK_TAGS_LENGTH).Sql
            .Should().Be(AssetListingSuggestionConfiguration.SqlTagsLength);
        entityType.GetCheckConstraints().Single(c => c.Name == AssetListingSuggestionConfiguration.CK_TAGS_ITEMS).Sql
            .Should().Be(AssetListingSuggestionConfiguration.SqlTagsItems);
        entityType.GetCheckConstraints().Single(c => c.Name == AssetListingSuggestionConfiguration.CK_TAGS_SIZE).Sql
            .Should().Be(AssetListingSuggestionConfiguration.SqlTagsSize);
        entityType.FindPrimaryKey()!.GetName().Should().Be(AssetListingSuggestionConfiguration.PRIMARY_KEY);

        IForeignKey fk = entityType.GetForeignKeys().Single(f => f.PrincipalEntityType.ClrType == typeof(AssetProcessingJob));
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
        fk.Properties.Select(p => p.Name).Should().Equal("JobId");
        entityType.FindProperty(nameof(AssetListingSuggestion.ContentHash))!
            .GetAnnotations().First(a => a.Name == "Relational:ColumnType").Value.Should().Be("char(64)");
        entityType.FindProperty(nameof(AssetListingSuggestion.Tags))!
            .GetAnnotations().First(a => a.Name == "Relational:ColumnType").Value.Should().Be("jsonb");
    }
}

public sealed class AssetConfigurationTests
{
    [Fact]
    public void AssetConfiguration_ShouldNotDefineGlobalQueryFilter()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new ApplicationDbContext(options);
        IModel model = dbContext.GetService<IDesignTimeModel>().Model;
        IEntityType? entityType = model.FindEntityType(typeof(Asset));
        entityType.Should().NotBeNull();
        entityType.GetDeclaredQueryFilters().Should().BeEmpty();
    }

    [Fact]
    public void Model_ShouldNotEmitQueryFilterWarningOnRequiredNavigations()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Throw(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning))
            .Options;

        using var dbContext = new ApplicationDbContext(options);
        Action act = () =>
        {
            _ = dbContext.AssetVersions.Include(v => v.Asset).ToList();
            _ = dbContext.Purchases.Include(p => p.Asset).ToList();
            _ = dbContext.AssetProcessingJobs.Include(j => j.Asset).ToList();
        };

        act.Should().NotThrow();
    }
}
