using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class SecurityRecheckConfiguration : IEntityTypeConfiguration<SecurityRecheck>
{
    public void Configure(EntityTypeBuilder<SecurityRecheck> builder)
    {
        builder.ToTable("security_rechecks", table =>
        {
            table.HasCheckConstraint("CK_security_rechecks_content_sha", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_security_rechecks_operation_digest", "\"OperationDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_security_rechecks_generation", "\"SecurityGeneration\" > 0");
            table.HasCheckConstraint("CK_security_rechecks_definition_version", "\"DefinitionVersion\" > 0");
            table.HasCheckConstraint(
                "CK_security_rechecks_state",
                "\"State\" IN ('QUEUED', 'INSPECTING', 'SCANNING', 'SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED')");
            table.HasCheckConstraint(
                "CK_security_rechecks_selected_generation",
                "\"SelectedGeneration\" IS NULL OR \"SelectedGeneration\" = \"SecurityGeneration\"");
            table.HasCheckConstraint(
                "CK_security_rechecks_terminal_completed_at",
                "(\"State\" IN ('SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED') AND \"CompletedAt\" IS NOT NULL) OR " +
                "(\"State\" NOT IN ('SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED') AND \"CompletedAt\" IS NULL)");
        });

        builder.HasKey(r => r.Id);

        builder.Property(r => r.AssetId).IsRequired();
        builder.Property(r => r.AssetVersionId).IsRequired();
        builder.Property(r => r.ContentSha256).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(r => r.SecurityGeneration).IsRequired();
        builder.Property(r => r.SecurityPolicyVersion).IsRequired().HasMaxLength(64);
        builder.Property(r => r.DefinitionVersion).IsRequired();
        builder.Property(r => r.OperationDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(r => r.OperationReceiptId).IsRequired();
        builder.Property(r => r.State)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(s => s.ToString(), s => Enum.Parse<SecurityRecheckState>(s));
        builder.Property(r => r.SelectedGeneration);
        builder.Property(r => r.ErrorCode).HasMaxLength(64);
        builder.Property(r => r.ErrorSummary).HasMaxLength(2000);
        builder.Property(r => r.RequestedAt);
        builder.Property(r => r.CompletedAt);
        builder.Property(r => r.CreatedAt).IsRequired();

        builder.HasOne(r => r.Asset).WithMany().HasForeignKey(r => r.AssetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.AssetVersion)
            .WithMany()
            .HasForeignKey(r => new { r.AssetId, r.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SecurityRecheck>()
            .WithMany()
            .HasForeignKey(r => r.ParentRecheckId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SecurityEvidenceRecord>()
            .WithMany()
            .HasForeignKey(r => r.ArchiveEvidenceId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<SecurityEvidenceRecord>()
            .WithMany()
            .HasForeignKey(r => r.MalwareEvidenceId)
            .OnDelete(DeleteBehavior.Restrict);

        // A generation is a new verification of the same bytes, unique per version.
        builder.HasIndex(r => new { r.AssetVersionId, r.SecurityGeneration })
            .IsUnique()
            .HasDatabaseName("UIX_security_rechecks_generation");

        // The operation receipt is the idempotency identity, so one receipt cannot be reused for a
        // different envelope or for a second generation. The envelope digest is deliberately not
        // unique: an explicit retry after a terminal failure legitimately repeats that envelope.
        builder.HasIndex(r => new { r.AssetVersionId, r.OperationReceiptId })
            .IsUnique()
            .HasDatabaseName("UIX_security_rechecks_operation_receipt");

        builder.HasIndex(r => new { r.AssetId, r.AssetVersionId, r.Id })
            .IsUnique()
            .HasDatabaseName("UIX_security_rechecks_scope");

        // At most one selected generation per version.
        builder.HasIndex(r => r.AssetVersionId)
            .IsUnique()
            .HasFilter("\"SelectedGeneration\" IS NOT NULL")
            .HasDatabaseName("UIX_security_rechecks_selected");
    }
}
