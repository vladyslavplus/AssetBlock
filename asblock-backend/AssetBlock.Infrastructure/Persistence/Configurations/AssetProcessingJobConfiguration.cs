using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AssetProcessingJobConfiguration : IEntityTypeConfiguration<AssetProcessingJob>
{
    public void Configure(EntityTypeBuilder<AssetProcessingJob> builder)
    {
        builder.ToTable("asset_processing_jobs", table =>
        {
            table.HasCheckConstraint("CK_asset_processing_jobs_attempt_count", "\"AttemptCount\" >= 0 AND \"AttemptCount\" <= \"MaxAttempts\"");
            table.HasCheckConstraint("CK_asset_processing_jobs_max_attempts", "\"MaxAttempts\" > 0 AND \"MaxAttempts\" <= 10");
            table.HasCheckConstraint("CK_asset_processing_jobs_definition_version", "\"DefinitionVersion\" > 0");
            table.HasCheckConstraint("CK_asset_processing_jobs_type", "\"Type\" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN', 'LISTING_COPILOT')");
            table.HasCheckConstraint("CK_asset_processing_jobs_status", "\"Status\" IN ('QUEUED', 'RUNNING', 'RETRY_SCHEDULED', 'SUCCEEDED', 'FAILED', 'CANCELLED')");
            table.HasCheckConstraint("CK_asset_processing_jobs_error_code", "\"ErrorCode\" IS NULL OR \"ErrorCode\" ~ '^[A-Z0-9_]{1,64}$'");
            
            // RUNNING requires lease fields; non-RUNNING must not have active lease
            table.HasCheckConstraint("CK_asset_processing_jobs_running_lease", 
                "(\"Status\" = 'RUNNING' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR " +
                "(\"Status\" != 'RUNNING' AND \"LeaseOwner\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");
                
            // Terminal status requires CompletedAt; non-terminal must not have it
            table.HasCheckConstraint("CK_asset_processing_jobs_terminal_completed_at", 
                "(\"Status\" IN ('SUCCEEDED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NOT NULL) OR " +
                "(\"Status\" NOT IN ('SUCCEEDED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NULL)");
                
            // Payload must be a JSON object and under 4000 bytes
            table.HasCheckConstraint("CK_asset_processing_jobs_payload_type", "jsonb_typeof(\"Payload\") = 'object'");
            table.HasCheckConstraint("CK_asset_processing_jobs_payload_size", "octet_length(CAST(\"Payload\" AS text)) <= 4000");

            // Result must be a JSON object (if not null) and under 4000 bytes
            table.HasCheckConstraint("CK_asset_processing_jobs_result_type", "\"Result\" IS NULL OR jsonb_typeof(\"Result\") = 'object'");
            table.HasCheckConstraint("CK_asset_processing_jobs_result_size", "\"Result\" IS NULL OR octet_length(CAST(\"Result\" AS text)) <= 4000");
        });

        builder.HasKey(j => j.Id);
        
        builder.Property(j => j.AssetId).IsRequired();
        builder.Property(j => j.AssetVersionId).IsRequired();
        
        builder.Property(j => j.Type)
            .IsRequired()
            .HasMaxLength(64)
            .HasConversion(
                t => t.ToString(),
                s => Enum.Parse<AssetProcessingJobType>(s));
                
        builder.Property(j => j.DefinitionVersion).IsRequired();
        
        builder.Property(j => j.Status)
            .IsRequired()
            .HasMaxLength(64)
            .HasConversion(
                s => s.ToString(),
                s => Enum.Parse<AssetProcessingJobStatus>(s));
                
        builder.Property(j => j.Stage).IsRequired().HasMaxLength(64);
        builder.Property(j => j.AttemptCount).IsRequired();
        builder.Property(j => j.MaxAttempts).IsRequired();
        builder.Property(j => j.AvailableAt).IsRequired();
        
        builder.Property(j => j.LeaseOwner).HasMaxLength(128);
        builder.Property(j => j.LeaseToken);
        builder.Property(j => j.LeaseExpiresAt);
        builder.Property(j => j.StartedAt);
        builder.Property(j => j.CompletedAt);
        
        builder.Property(j => j.ErrorCode).HasMaxLength(64);
        builder.Property(j => j.ErrorSummary).HasMaxLength(2000);
        builder.Property(j => j.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(j => j.Result).HasColumnType("jsonb");
        builder.Property(j => j.TraceParent).HasMaxLength(128);
        
        builder.Property(j => j.CreatedAt).IsRequired();
        builder.Property(j => j.UpdatedAt);

        builder.HasOne(j => j.Asset)
            .WithMany()
            .HasForeignKey(j => j.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        // Ensures jobs are tied to an exact version using the composite key
        builder.HasOne(j => j.AssetVersion)
            .WithMany()
            .HasForeignKey(j => new { j.AssetId, j.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Cascade);

        // Idempotency: exact same job cannot be enqueued twice for the same version
        builder.HasIndex(j => new { j.AssetVersionId, j.Type, j.DefinitionVersion })
            .IsUnique()
            .HasDatabaseName("UIX_asset_processing_jobs_idempotency");

        // Queue order and claim index
        builder.HasIndex(j => new { j.Status, j.AvailableAt, j.Id })
            .HasDatabaseName("IX_asset_processing_jobs_claim");

        // Fast lookup for expired lease recovery
        builder.HasIndex(j => j.LeaseExpiresAt)
            .HasDatabaseName("IX_asset_processing_jobs_lease_expiry");
    }
}
