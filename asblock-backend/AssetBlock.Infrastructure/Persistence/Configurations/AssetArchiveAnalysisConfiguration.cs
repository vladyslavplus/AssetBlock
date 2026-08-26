using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AssetArchiveAnalysisConfiguration : IEntityTypeConfiguration<AssetArchiveAnalysis>
{
    public void Configure(EntityTypeBuilder<AssetArchiveAnalysis> builder)
    {
        builder.ToTable("asset_archive_analyses", table =>
        {
            table.HasCheckConstraint("CK_asset_archive_analyses_file_count", "\"FileCount\" >= 0");
            table.HasCheckConstraint("CK_asset_archive_analyses_total_expanded_bytes", "\"TotalExpandedBytes\" >= 0");
            table.HasCheckConstraint(
                "CK_asset_archive_analyses_readme_content_size",
                "\"ReadmeContent\" IS NULL OR octet_length(\"ReadmeContent\") <= 16384");
            table.HasCheckConstraint(
                "CK_asset_archive_analyses_manifest_metadata",
                "\"ManifestMetadata\" IS NULL OR jsonb_typeof(\"ManifestMetadata\") = 'object'");
            table.HasCheckConstraint(
                "CK_asset_archive_analyses_manifest_metadata_size",
                "\"ManifestMetadata\" IS NULL OR octet_length(CAST(\"ManifestMetadata\" AS text)) <= 16384");
        });

        builder.HasKey(a => a.AssetVersionId);

        builder.Property(a => a.FileCount).IsRequired();
        builder.Property(a => a.TotalExpandedBytes).IsRequired();
        builder.Property(a => a.ReadmeContent).HasMaxLength(16384);
        builder.Property(a => a.ManifestMetadata).HasColumnType("jsonb");
        builder.Property(a => a.CreatedAt).IsRequired();
        builder.Property(a => a.UpdatedAt);

        builder.HasOne(a => a.AssetVersion)
            .WithOne(v => v.ArchiveAnalysis)
            .HasForeignKey<AssetArchiveAnalysis>(a => a.AssetVersionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
