using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CodeAnalysisRunConfiguration : IEntityTypeConfiguration<CodeAnalysisRun>
{
    public void Configure(EntityTypeBuilder<CodeAnalysisRun> builder)
    {
        builder.ToTable("code_analysis_runs", table =>
        {
            table.HasCheckConstraint("CK_code_analysis_runs_content_sha", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_runs_input_digest", "\"InputDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_runs_policy_digest", "\"PolicyDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_runs_declaration_digest", "\"DeclarationDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_runs_material_digest", "\"MaterialDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_runs_selection_generation", "\"SelectionGeneration\" > 0");
            table.HasCheckConstraint("CK_code_analysis_runs_security_generation", "\"SecurityGeneration\" > 0");
            table.HasCheckConstraint("CK_code_analysis_runs_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
            table.HasCheckConstraint(
                "CK_code_analysis_runs_transport_lease_token",
                "\"TransportLeaseToken\" IS NULL OR \"TransportLeaseToken\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "CK_code_analysis_runs_state",
                "\"State\" IN ('QUEUED', 'RUNNING', 'COMPLETED', 'FAILED', 'CANCELLED')");
            table.HasCheckConstraint(
                "CK_code_analysis_runs_disposition",
                "\"Disposition\" IS NULL OR \"Disposition\" IN ('SELECTED', 'HISTORICAL_SUPERSEDED', 'HISTORICAL_STALE_INPUT', 'HISTORICAL_INCOMPLETE')");
            table.HasCheckConstraint(
                "CK_code_analysis_runs_terminal_completed_at",
                "(\"State\" IN ('COMPLETED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NOT NULL) OR " +
                "(\"State\" NOT IN ('COMPLETED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NULL)");
            // Reservation identity is all-or-nothing so a run is never half bound to a slot.
            table.HasCheckConstraint(
                "CK_code_analysis_runs_reservation",
                "((\"TransportLeaseToken\" IS NULL AND \"ReservationGeneration\" IS NULL AND \"HardDeadlineUtc\" IS NULL) OR " +
                "(\"TransportLeaseToken\" IS NOT NULL AND \"ReservationGeneration\" IS NOT NULL AND \"HardDeadlineUtc\" IS NOT NULL))");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.AssetId).IsRequired();
        builder.Property(r => r.AssetVersionId).IsRequired();
        builder.Property(r => r.ContentSha256).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(r => r.PolicyDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(r => r.DeclarationDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(r => r.MaterialDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(r => r.InputDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(r => r.SelectionGeneration).IsRequired();
        builder.Property(r => r.SecurityGeneration).IsRequired();
        builder.Property(r => r.ServiceKey).IsRequired().HasMaxLength(128);
        builder.Property(r => r.TransportLeaseToken).HasColumnType("char(64)").IsFixedLength();
        builder.Property(r => r.HardDeadlineUtc);
        builder.Property(r => r.State)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(s => s.ToString(), s => Enum.Parse<CodeAnalysisRunState>(s));
        builder.Property(r => r.Disposition)
            .HasMaxLength(32)
            .HasConversion(s => s.ToString(), s => Enum.Parse<CodeAnalysisReportDisposition>(s!));
        builder.Property(r => r.ErrorCode).HasMaxLength(64);
        builder.Property(r => r.ErrorSummary).HasMaxLength(2000);
        builder.Property(r => r.OwnerRetryable).IsRequired().HasDefaultValue(false);
        builder.Property(r => r.StartedAt);
        builder.Property(r => r.CompletedAt);
        builder.Property(r => r.CreatedAt).IsRequired();

        builder.HasOne(r => r.Asset).WithMany().HasForeignKey(r => r.AssetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.AssetVersion)
            .WithMany()
            .HasForeignKey(r => new { r.AssetId, r.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AssetDraftWorkspace>().WithMany().HasForeignKey(r => r.AssetDraftWorkspaceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CodeAnalysisPolicyVersion>().WithMany().HasForeignKey(r => r.PolicyVersionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AssetSourceDeclarationRevision>().WithMany().HasForeignKey(r => r.DeclarationRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AssetMaterialMetadataRevision>().WithMany().HasForeignKey(r => r.MaterialRevisionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SecurityRecheck>().WithMany().HasForeignKey(r => r.SecurityRecheckId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SecurityEvidenceRecord>().WithMany().HasForeignKey(r => r.ArchiveEvidenceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<SecurityEvidenceRecord>().WithMany().HasForeignKey(r => r.MalwareEvidenceId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CodeAnalysisRun>().WithMany().HasForeignKey(r => r.ParentRunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AssetProcessingJob>().WithMany().HasForeignKey(r => r.JobId).OnDelete(DeleteBehavior.Restrict);

        // The immutable computational identity of an analysis input.
        builder.HasIndex(r => r.InputDigest)
            .IsUnique()
            .HasDatabaseName("UIX_code_analysis_runs_input_digest");

        builder.HasIndex(r => new { r.AssetId, r.AssetVersionId, r.Id })
            .IsUnique()
            .HasDatabaseName("UIX_code_analysis_runs_scope");

        // One live run per selection generation; superseded runs stay as history.
        builder.HasIndex(r => new { r.AssetVersionId, r.SelectionGeneration })
            .IsUnique()
            .HasFilter("\"State\" IN ('QUEUED', 'RUNNING')")
            .HasDatabaseName("UIX_code_analysis_runs_active_generation");

        // One live run per exact immutable input tuple. The tuple deliberately excludes RunId, so a
        // duplicate enqueue resolves to the existing run instead of racing on a per-run digest.
        builder.HasIndex(r => new
        {
            r.AssetId,
            r.AssetVersionId,
            r.ContentSha256,
            r.PolicyDigest,
            r.DeclarationDigest,
            r.MaterialDigest,
            r.SelectionGeneration,
            r.SecurityGeneration,
            r.ArchiveEvidenceId,
            r.MalwareEvidenceId
        })
            .IsUnique()
            .HasFilter("\"State\" IN ('QUEUED', 'RUNNING')")
            .HasDatabaseName("UIX_code_analysis_runs_active_input");

        builder.HasIndex(r => r.JobId)
            .IsUnique()
            .HasFilter("\"JobId\" IS NOT NULL")
            .HasDatabaseName("UIX_code_analysis_runs_job");
    }
}
