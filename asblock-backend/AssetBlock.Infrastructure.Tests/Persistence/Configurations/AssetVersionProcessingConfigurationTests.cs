using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AssetBlock.Infrastructure.Tests.Persistence.Configurations;

public sealed class AssetVersionProcessingConfigurationTests
{
    [Fact]
    public void AssetVersionConfiguration_ShouldIncludeProcessingLifecycleFieldsAndConstraints()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new ApplicationDbContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;

        var entityType = model.FindEntityType(typeof(AssetVersion));
        entityType.Should().NotBeNull();

        entityType.GetTableName().Should().Be("asset_versions");

        var statusCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_versions_processing_status");
        statusCheck.Should().NotBeNull();
        statusCheck.Sql.Should().Be("\"ProcessingStatus\" IN ('PENDING_INSPECTION', 'PENDING_MALWARE_SCAN', 'READY', 'REJECTED', 'PROCESSING_FAILED')");

        var errorCodeCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_versions_processing_error_code");
        errorCodeCheck.Should().NotBeNull();
        errorCodeCheck.Sql.Should().Be("\"ProcessingErrorCode\" IS NULL OR \"ProcessingErrorCode\" ~ '^[A-Z0-9_]{1,64}$'");

        var readyCurrentCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_versions_ready_current");
        readyCurrentCheck.Should().NotBeNull();
        readyCurrentCheck.Sql.Should().Be("\"IsCurrent\" = false OR (\"IsCurrent\" = true AND \"ProcessingStatus\" = 'READY')");

        var consistencyCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_versions_state_error_consistency");
        consistencyCheck.Should().NotBeNull();
        consistencyCheck.Sql.Should().Be("(\"ProcessingStatus\" IN ('PENDING_INSPECTION', 'PENDING_MALWARE_SCAN', 'READY') AND \"ProcessingErrorCode\" IS NULL AND \"ProcessingErrorSummary\" IS NULL) OR (\"ProcessingStatus\" IN ('REJECTED', 'PROCESSING_FAILED') AND \"ProcessingErrorCode\" IS NOT NULL AND \"ProcessingErrorSummary\" IS NOT NULL AND length(trim(\"ProcessingErrorSummary\")) > 0)");

        entityType.FindProperty(nameof(AssetVersion.ProcessingStatus))!.GetMaxLength().Should().Be(64);
        entityType.FindProperty(nameof(AssetVersion.ProcessingErrorCode))!.GetMaxLength().Should().Be(64);
        entityType.FindProperty(nameof(AssetVersion.ProcessingErrorSummary))!.GetMaxLength().Should().Be(2000);
        entityType.FindProperty(nameof(AssetVersion.ProcessingUpdatedAt))!.IsNullable.Should().BeFalse();

        var statusIndex = entityType.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_asset_versions_processing_status");
        statusIndex.Should().NotBeNull();
        statusIndex.Properties.Select(p => p.Name).Should().BeEquivalentTo(nameof(AssetVersion.ProcessingStatus));
    }

    [Fact]
    public void AssetArchiveAnalysisConfiguration_ShouldSetCorrectMetadataAndConstraints()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new ApplicationDbContext(options);
        var model = dbContext.GetService<IDesignTimeModel>().Model;

        var entityType = model.FindEntityType(typeof(AssetArchiveAnalysis));
        entityType.Should().NotBeNull();

        entityType.GetTableName().Should().Be("asset_archive_analyses");

        var fileCountCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_archive_analyses_file_count");
        fileCountCheck.Should().NotBeNull();
        fileCountCheck.Sql.Should().Be("\"FileCount\" >= 0");

        var totalBytesCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_archive_analyses_total_expanded_bytes");
        totalBytesCheck.Should().NotBeNull();
        totalBytesCheck.Sql.Should().Be("\"TotalExpandedBytes\" >= 0");

        var readmeBytesCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_archive_analyses_readme_content_size");
        readmeBytesCheck.Should().NotBeNull();
        readmeBytesCheck.Sql.Should().Be("\"ReadmeContent\" IS NULL OR octet_length(\"ReadmeContent\") <= 16384");

        var manifestCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_archive_analyses_manifest_metadata");
        manifestCheck.Should().NotBeNull();
        manifestCheck.Sql.Should().Be("\"ManifestMetadata\" IS NULL OR jsonb_typeof(\"ManifestMetadata\") = 'object'");

        var manifestBytesCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_archive_analyses_manifest_metadata_size");
        manifestBytesCheck.Should().NotBeNull();
        manifestBytesCheck.Sql.Should().Be("\"ManifestMetadata\" IS NULL OR octet_length(CAST(\"ManifestMetadata\" AS text)) <= 16384");

        entityType.FindProperty(nameof(AssetArchiveAnalysis.ReadmeContent))!.GetMaxLength().Should().Be(16384);
        var manifestAnnotation = entityType.FindProperty(nameof(AssetArchiveAnalysis.ManifestMetadata))!.GetAnnotations().FirstOrDefault(a => a.Name == "Relational:ColumnType");
        manifestAnnotation.Should().NotBeNull();
        manifestAnnotation.Value.Should().Be("jsonb");

        var fk = entityType.GetForeignKeys().FirstOrDefault(f => f.PrincipalEntityType.ClrType == typeof(AssetVersion));
        fk.Should().NotBeNull();
        fk.Properties.Select(p => p.Name).Should().BeEquivalentTo(nameof(AssetArchiveAnalysis.AssetVersionId));
        fk.DeleteBehavior.Should().Be(DeleteBehavior.Cascade);
    }
}
