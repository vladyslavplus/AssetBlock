using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AssetVersionConfiguration : IEntityTypeConfiguration<AssetVersion>
{
    public void Configure(EntityTypeBuilder<AssetVersion> builder)
    {
        builder.ToTable("asset_versions", table =>
        {
            table.HasCheckConstraint("CK_asset_versions_version_number_positive", "\"VersionNumber\" > 0");
            table.HasCheckConstraint("CK_asset_versions_content_length_positive", "\"ContentLength\" > 0");
            table.HasCheckConstraint(
                "CK_asset_versions_processing_status",
                "\"ProcessingStatus\" IN ('PENDING_INSPECTION', 'PENDING_MALWARE_SCAN', 'READY', 'REJECTED', 'PROCESSING_FAILED')");
            table.HasCheckConstraint(
                "CK_asset_versions_processing_error_code",
                "\"ProcessingErrorCode\" IS NULL OR \"ProcessingErrorCode\" ~ '^[A-Z0-9_]{1,64}$'");
            table.HasCheckConstraint(
                "CK_asset_versions_ready_current",
                "\"IsCurrent\" = false OR (\"IsCurrent\" = true AND \"ProcessingStatus\" = 'READY')");
            table.HasCheckConstraint(
                "CK_asset_versions_state_error_consistency",
                "(\"ProcessingStatus\" IN ('PENDING_INSPECTION', 'PENDING_MALWARE_SCAN', 'READY') AND \"ProcessingErrorCode\" IS NULL AND \"ProcessingErrorSummary\" IS NULL) OR (\"ProcessingStatus\" IN ('REJECTED', 'PROCESSING_FAILED') AND \"ProcessingErrorCode\" IS NOT NULL AND \"ProcessingErrorSummary\" IS NOT NULL AND length(trim(\"ProcessingErrorSummary\")) > 0)");
            table.HasCheckConstraint("CK_asset_versions_safety_restriction", "\"SafetyRestriction\" IN ('NONE', 'UNSAFE_ARCHIVE', 'INFECTED', 'INTEGRITY_MISMATCH')");
            table.HasCheckConstraint("CK_asset_versions_analysis_selection_generation", "\"AnalysisSelectionGeneration\" > 0");
            // A restriction is always explained; absence of a reason means absence of a restriction.
            table.HasCheckConstraint(
                "CK_asset_versions_safety_restriction_reason",
                "(\"SafetyRestriction\" = 'NONE' AND \"SafetyRestrictionReason\" IS NULL AND \"SafetyRestrictedAt\" IS NULL) OR " +
                "(\"SafetyRestriction\" <> 'NONE' AND \"SafetyRestrictionReason\" IS NOT NULL AND \"SafetyRestrictedAt\" IS NOT NULL)");
        });

        builder.HasKey(v => v.Id);
        builder.Property(v => v.AssetId).IsRequired();
        builder.Property(v => v.VersionNumber).IsRequired();
        builder.Property(v => v.IsCurrent).IsRequired();
        builder.Property(v => v.StorageKey).IsRequired().HasMaxLength(1024);
        builder.Property(v => v.StorageVersionId).HasMaxLength(1024);
        builder.Property(v => v.StorageRetiredAt);
        builder.Property(v => v.FileName).IsRequired().HasMaxLength(512);
        builder.Property(v => v.ContentLength).IsRequired();
        builder.Property(v => v.ContentSha256).IsRequired().HasMaxLength(64);
        builder.Property(v => v.ReleaseNotes).IsRequired().HasMaxLength(4000);

        builder.Property(v => v.LicenseCode)
            .IsRequired()
            .HasMaxLength(64)
            .HasConversion(
                code => code.ToString(),
                raw => Enum.Parse<AssetLicenseCode>(raw));

        builder.Property(v => v.LicenseTemplateVersion).IsRequired().HasMaxLength(32);
        builder.Property(v => v.LicenseDisplayName).IsRequired().HasMaxLength(128);
        builder.Property(v => v.LicenseTerms).IsRequired().HasMaxLength(16000);

        builder.Property(v => v.ProcessingStatus)
            .IsRequired()
            .HasMaxLength(64)
            .HasConversion(
                status => status.ToString(),
                raw => Enum.Parse<AssetVersionProcessingStatus>(raw));

        builder.Property(v => v.ProcessingErrorCode).HasMaxLength(64);
        builder.Property(v => v.ProcessingErrorSummary).HasMaxLength(2000);
        builder.Property(v => v.ProcessingUpdatedAt).IsRequired();
        // Both defaults must satisfy the new check constraints, because the columns are added to a
        // table that already holds approved and purchased versions.
        builder.Property(v => v.SafetyRestriction)
            .IsRequired()
            .HasMaxLength(24)
            .HasConversion(s => s.ToString(), s => Enum.Parse<AssetVersionSafetyRestriction>(s));
        builder.Property(v => v.SafetyRestriction).Metadata.SetDefaultValue(AssetVersionSafetyRestriction.NONE);
        builder.Property(v => v.SafetyRestrictionReason).HasMaxLength(64);
        builder.Property(v => v.AnalysisSelectionGeneration).IsRequired().HasDefaultValue(1);
        builder.Property(v => v.AnalysisSelectionGeneration).IsRequired();

        builder.Property(v => v.CreatedAt).IsRequired();
        // Versions are append-only; UpdatedAt from BaseEntity is unused and ignored.
        builder.Ignore(v => v.UpdatedAt);

        // Only current-version pointer and processing lifecycle fields change after insert. Content and license snapshot stay immutable.
        builder.Property(v => v.StorageKey).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.StorageVersionId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.FileName).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.ContentLength).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.ContentSha256).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.ReleaseNotes).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.LicenseCode).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.LicenseTemplateVersion).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.LicenseDisplayName).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.LicenseTerms).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.SelectedCodeAnalysisReportId).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        builder.HasOne(v => v.Asset)
            .WithMany(a => a.Versions)
            .HasForeignKey(v => v.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

        // The selected report is restrictive: deletion or tombstoning must clear it explicitly first.
        builder.HasOne<CodeAnalysisReportHeader>()
            .WithMany()
            .HasForeignKey(v => v.SelectedCodeAnalysisReportId)
            .OnDelete(DeleteBehavior.Restrict);

        // Enables composite FKs so child rows cannot reference a version of a different asset.
        builder.HasAlternateKey(v => new { v.AssetId, v.Id })
            .HasName("AK_asset_versions_AssetId_Id");

        // One current version per asset.
        builder.HasIndex(v => v.AssetId)
            .HasFilter("\"IsCurrent\" = true")
            .IsUnique()
            .HasDatabaseName("UIX_asset_versions_asset_current");

        // Deterministic version number per asset.
        builder.HasIndex(v => new { v.AssetId, v.VersionNumber })
            .IsUnique()
            .HasDatabaseName("UIX_asset_versions_asset_number");

        builder.HasIndex(v => v.StorageKey)
            .IsUnique()
            .HasDatabaseName("UIX_asset_versions_storage_key");

        builder.HasIndex(v => v.ProcessingStatus)
            .HasDatabaseName("IX_asset_versions_processing_status");
    }
}
