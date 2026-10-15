using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class SecurityEvidenceRecordConfiguration : IEntityTypeConfiguration<SecurityEvidenceRecord>
{
    public void Configure(EntityTypeBuilder<SecurityEvidenceRecord> builder)
    {
        builder.ToTable("security_evidence_records", table =>
        {
            table.HasCheckConstraint("CK_security_evidence_records_content_sha", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_security_evidence_records_digest", "\"EvidenceDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_security_evidence_records_kind", "\"EvidenceKind\" IN ('ARCHIVE_VALIDATION', 'MALWARE_SCAN')");
            table.HasCheckConstraint(
                "CK_security_evidence_records_outcome",
                "\"Outcome\" IN ('VALID', 'CLEAN', 'UNSAFE', 'INFECTED', 'ERROR')");
            table.HasCheckConstraint("CK_security_evidence_records_generation", "\"Generation\" > 0");
            table.HasCheckConstraint("CK_security_evidence_records_scanned_bytes", "\"ScannedByteCount\" >= 0");
            // Identity is kind aware: archive validation is identified by the implementation that ran
            // it, malware by the observed engine and signature. An identity that could not be observed
            // is recorded as unavailable for malware and never synthesized from the other kind.
            table.HasCheckConstraint(
                "CK_security_evidence_records_identity",
                "(\"EvidenceKind\" = 'ARCHIVE_VALIDATION' AND \"ImplementationIdentity\" IS NOT NULL AND \"EngineIdentity\" IS NULL) OR " +
                "(\"EvidenceKind\" = 'MALWARE_SCAN' AND \"ImplementationIdentity\" IS NULL AND ((\"IdentityAvailable\" AND \"EngineIdentity\" IS NOT NULL) OR (NOT \"IdentityAvailable\" AND \"EngineIdentity\" IS NULL)))");
            table.HasCheckConstraint(
                "CK_security_evidence_records_signature_scope",
                "(\"IdentityAvailable\" AND \"SignatureObservedAt\" IS NOT NULL AND \"SignatureValidUntil\" IS NOT NULL) OR (NOT \"IdentityAvailable\" AND \"SignatureObservedAt\" IS NULL AND \"SignatureValidUntil\" IS NULL)");
        });

        builder.HasKey(e => e.Id);

        builder.Property(e => e.AssetId).IsRequired();
        builder.Property(e => e.AssetVersionId).IsRequired();
        builder.Property(e => e.ContentSha256).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(e => e.EvidenceKind)
            .IsRequired()
            .HasMaxLength(24)
            .HasConversion(s => s.ToString(), s => Enum.Parse<SecurityEvidenceKind>(s));
        builder.Property(e => e.Generation).IsRequired();
        builder.Property(e => e.DefinitionVersion).IsRequired();
        builder.Property(e => e.Outcome)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(s => s.ToString(), s => Enum.Parse<SecurityEvidenceOutcome>(s));
        builder.Property(e => e.SecurityPolicyVersion).IsRequired().HasMaxLength(64);
        builder.Property(e => e.ImplementationIdentity).HasMaxLength(128);
        builder.Property(e => e.EngineIdentity).HasMaxLength(128);
        builder.Property(e => e.EngineVersion).HasMaxLength(64);
        builder.Property(e => e.SignatureIdentity).HasMaxLength(256);
        builder.Property(e => e.SignatureObservedAt);
        builder.Property(e => e.SignatureValidUntil);
        builder.Property(e => e.IdentityAvailable).IsRequired();
        builder.Property(e => e.ScannedByteCount).IsRequired();
        builder.Property(e => e.ScannedEntryCount);
        builder.Property(e => e.ScopeComplete).IsRequired();
        builder.Property(e => e.CompletedAt).IsRequired();
        builder.Property(e => e.EvidenceDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(e => e.CreatedAt).IsRequired();

        builder.HasOne<SecurityRecheck>()
            .WithMany()
            .HasForeignKey(e => e.SecurityRecheckId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<AssetProcessingJob>()
            .WithMany()
            .HasForeignKey(e => e.JobId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.EvidenceDigest)
            .IsUnique()
            .HasDatabaseName("UIX_security_evidence_records_digest");

        // One evidence row per kind per recheck generation.
        builder.HasIndex(e => new { e.SecurityRecheckId, e.EvidenceKind })
            .IsUnique()
            .HasFilter("\"SecurityRecheckId\" IS NOT NULL")
            .HasDatabaseName("UIX_security_evidence_records_recheck_kind");

        // Original initial-upload evidence keeps exactly one row per kind per version.
        builder.HasIndex(e => new { e.AssetVersionId, e.EvidenceKind })
            .IsUnique()
            .HasFilter("\"SecurityRecheckId\" IS NULL")
            .HasDatabaseName("UIX_security_evidence_records_upload_kind");
    }
}
