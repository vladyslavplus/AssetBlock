using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AssetBlock.Infrastructure.Tests.Persistence.Configurations;

public sealed class AssetProcessingJobConfigurationTests
{
    [Fact]
    public void Configuration_ShouldSetCorrectMetadataAndConstraints()
    {
        DbContextOptions<ApplicationDbContext> options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var dbContext = new ApplicationDbContext(options);
        IModel model = dbContext.GetService<IDesignTimeModel>().Model;

        IEntityType? entityType = model.FindEntityType(typeof(AssetProcessingJob));
        entityType.Should().NotBeNull();

        entityType.GetTableName().Should().Be("asset_processing_jobs");

        ICheckConstraint? attemptCountCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_attempt_count");
        attemptCountCheck.Should().NotBeNull();
        attemptCountCheck.Sql.Should().Be("\"AttemptCount\" >= 0 AND \"AttemptCount\" <= \"MaxAttempts\"");

        ICheckConstraint? maxAttemptsCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_max_attempts");
        maxAttemptsCheck.Should().NotBeNull();
        maxAttemptsCheck.Sql.Should().Be("\"MaxAttempts\" > 0 AND \"MaxAttempts\" <= 10");

        ICheckConstraint? definitionCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_definition_version");
        definitionCheck.Should().NotBeNull();
        definitionCheck.Sql.Should().Be("\"DefinitionVersion\" > 0");

        ICheckConstraint? typeCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_type");
        typeCheck.Should().NotBeNull();
        typeCheck.Sql.Should().Be("\"Type\" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN', 'LISTING_COPILOT', 'EMBEDDING_GENERATION')");

        ICheckConstraint? embeddingHashesCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_embedding_hashes");
        embeddingHashesCheck.Should().NotBeNull();
        embeddingHashesCheck.Sql.Should().Be("(\"Type\" = 'EMBEDDING_GENERATION' AND \"InputHash\" IS NOT NULL AND \"InputHash\" ~ '^[0-9a-f]{64}$' AND \"ModelKey\" IS NOT NULL AND \"ModelKey\" ~ '^[0-9a-f]{64}$') OR (\"Type\" <> 'EMBEDDING_GENERATION' AND \"InputHash\" IS NULL AND \"ModelKey\" IS NULL)");

        ICheckConstraint? statusCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_status");
        statusCheck.Should().NotBeNull();
        statusCheck.Sql.Should().Be("\"Status\" IN ('QUEUED', 'RUNNING', 'RETRY_SCHEDULED', 'SUCCEEDED', 'FAILED', 'CANCELLED')");

        ICheckConstraint? errorCodeCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_error_code");
        errorCodeCheck.Should().NotBeNull();
        errorCodeCheck.Sql.Should().Be("\"ErrorCode\" IS NULL OR \"ErrorCode\" ~ '^[A-Z0-9_]{1,64}$'");

        ICheckConstraint? runningLeaseCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_running_lease");
        runningLeaseCheck.Should().NotBeNull();
        runningLeaseCheck.Sql.Should().Be("(\"Status\" = 'RUNNING' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR (\"Status\" != 'RUNNING' AND \"LeaseOwner\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");

        ICheckConstraint? terminalCompletedCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_terminal_completed_at");
        terminalCompletedCheck.Should().NotBeNull();
        terminalCompletedCheck.Sql.Should().Be("(\"Status\" IN ('SUCCEEDED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" NOT IN ('SUCCEEDED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NULL)");

        ICheckConstraint? payloadTypeCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_payload_type");
        payloadTypeCheck.Should().NotBeNull();
        payloadTypeCheck.Sql.Should().Be("jsonb_typeof(\"Payload\") = 'object'");

        ICheckConstraint? payloadSizeCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_payload_size");
        payloadSizeCheck.Should().NotBeNull();
        payloadSizeCheck.Sql.Should().Be("octet_length(CAST(\"Payload\" AS text)) <= 4000");

        ICheckConstraint? resultTypeCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_result_type");
        resultTypeCheck.Should().NotBeNull();
        resultTypeCheck.Sql.Should().Be("\"Result\" IS NULL OR jsonb_typeof(\"Result\") = 'object'");

        ICheckConstraint? resultSizeCheck = entityType.GetCheckConstraints().FirstOrDefault(c => c.Name == "CK_asset_processing_jobs_result_size");
        resultSizeCheck.Should().NotBeNull();
        resultSizeCheck.Sql.Should().Be("\"Result\" IS NULL OR octet_length(CAST(\"Result\" AS text)) <= 4000");

        entityType.FindProperty(nameof(AssetProcessingJob.Type))!.GetMaxLength().Should().Be(64);
        entityType.FindProperty(nameof(AssetProcessingJob.Status))!.GetMaxLength().Should().Be(64);
        entityType.FindProperty(nameof(AssetProcessingJob.Stage))!.GetMaxLength().Should().Be(64);
        entityType.FindProperty(nameof(AssetProcessingJob.LeaseOwner))!.GetMaxLength().Should().Be(128);
        entityType.FindProperty(nameof(AssetProcessingJob.ErrorCode))!.GetMaxLength().Should().Be(64);
        entityType.FindProperty(nameof(AssetProcessingJob.ErrorSummary))!.GetMaxLength().Should().Be(2000);
        IAnnotation? payloadAnnotation = entityType.FindProperty(nameof(AssetProcessingJob.Payload))!.GetAnnotations().FirstOrDefault(a => a.Name == "Relational:ColumnType");
        payloadAnnotation.Should().NotBeNull();
        payloadAnnotation.Value.Should().Be("jsonb");

        IAnnotation? resultAnnotation = entityType.FindProperty(nameof(AssetProcessingJob.Result))!.GetAnnotations().FirstOrDefault(a => a.Name == "Relational:ColumnType");
        resultAnnotation.Should().NotBeNull();
        resultAnnotation.Value.Should().Be("jsonb");
        entityType.FindProperty(nameof(AssetProcessingJob.TraceParent))!.GetMaxLength().Should().Be(128);
        IAnnotation? inputHashAnnotation = entityType.FindProperty(nameof(AssetProcessingJob.InputHash))!.GetAnnotations().FirstOrDefault(a => a.Name == "Relational:ColumnType");
        inputHashAnnotation.Should().NotBeNull();
        inputHashAnnotation.Value.Should().Be("char(64)");

        IAnnotation? modelKeyAnnotation = entityType.FindProperty(nameof(AssetProcessingJob.ModelKey))!.GetAnnotations().FirstOrDefault(a => a.Name == "Relational:ColumnType");
        modelKeyAnnotation.Should().NotBeNull();
        modelKeyAnnotation.Value.Should().Be("char(64)");

        IIndex? idempotencyIndex = entityType.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "UIX_asset_processing_jobs_idempotency");
        idempotencyIndex.Should().NotBeNull();
        idempotencyIndex.IsUnique.Should().BeTrue();
        idempotencyIndex.GetFilter().Should().Be("\"Type\" <> 'EMBEDDING_GENERATION'");
        idempotencyIndex.Properties.Select(p => p.Name).Should().BeEquivalentTo("AssetVersionId", "Type", "DefinitionVersion");

        IIndex? embeddingActiveIndex = entityType.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "UIX_asset_processing_jobs_embedding_active");
        embeddingActiveIndex.Should().NotBeNull();
        embeddingActiveIndex.IsUnique.Should().BeTrue();
        embeddingActiveIndex.GetFilter().Should().Be("\"Type\" = 'EMBEDDING_GENERATION' AND \"Status\" IN ('QUEUED', 'RUNNING', 'RETRY_SCHEDULED')");
        embeddingActiveIndex.Properties.Select(p => p.Name).Should().BeEquivalentTo("AssetId", "Type", "DefinitionVersion", "ModelKey", "InputHash");

        IIndex? claimIndex = entityType.GetIndexes().FirstOrDefault(i => i.GetDatabaseName() == "IX_asset_processing_jobs_claim");
        claimIndex.Should().NotBeNull();
        claimIndex.Properties.Select(p => p.Name).Should().BeEquivalentTo("Status", "AvailableAt", "Id");

        IForeignKey? fk = entityType.GetForeignKeys().FirstOrDefault(f => f.PrincipalEntityType.ClrType == typeof(AssetVersion));
        fk.Should().NotBeNull();
        fk.Properties.Select(p => p.Name).Should().BeEquivalentTo("AssetId", "AssetVersionId");
    }
}
