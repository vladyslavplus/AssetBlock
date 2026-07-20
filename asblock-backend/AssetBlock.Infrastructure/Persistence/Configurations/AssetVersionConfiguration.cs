using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AssetVersionConfiguration : IEntityTypeConfiguration<AssetVersion>
{
    public void Configure(EntityTypeBuilder<AssetVersion> builder)
    {
        builder.ToTable("asset_versions", table =>
        {
            table.HasCheckConstraint("CK_asset_versions_version_number_positive", "\"VersionNumber\" > 0");
            table.HasCheckConstraint("CK_asset_versions_content_length_positive", "\"ContentLength\" > 0");
        });

        builder.HasKey(v => v.Id);
        builder.Property(v => v.AssetId).IsRequired();
        builder.Property(v => v.VersionNumber).IsRequired();
        builder.Property(v => v.IsCurrent).IsRequired();
        builder.Property(v => v.StorageKey).IsRequired().HasMaxLength(1024);
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

        builder.Property(v => v.CreatedAt).IsRequired();
        // Versions are append-only; UpdatedAt from BaseEntity is unused and ignored.
        builder.Ignore(v => v.UpdatedAt);

        // Only current-version pointer changes after insert. Content and license snapshot stay immutable.
        builder.Property(v => v.StorageKey).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.FileName).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.ContentLength).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.ContentSha256).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.ReleaseNotes).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.LicenseCode).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.LicenseTemplateVersion).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.LicenseDisplayName).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);
        builder.Property(v => v.LicenseTerms).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Throw);

        builder.HasOne(v => v.Asset)
            .WithMany(a => a.Versions)
            .HasForeignKey(v => v.AssetId)
            .OnDelete(DeleteBehavior.Cascade);

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
    }
}
