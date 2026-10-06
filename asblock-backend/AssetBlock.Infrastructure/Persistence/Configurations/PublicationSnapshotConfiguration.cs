using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class PublicationSnapshotConfiguration : IEntityTypeConfiguration<PublicationSnapshot>
{
    public void Configure(EntityTypeBuilder<PublicationSnapshot> builder)
    {
        builder.ToTable("publication_snapshots", table =>
        {
            table.HasCheckConstraint("CK_publication_snapshots_sha", "length(\"ContentSha256\") = 64");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.ContentSha256).IsRequired().HasColumnType("char(64)");
        builder.Property(s => s.PolicyVersion).IsRequired().HasMaxLength(64);
        builder.Property(s => s.ApprovedMetadataJson).HasColumnType("jsonb").IsRequired();
        builder.Property(s => s.RightsReferenceJson).HasColumnType("jsonb").IsRequired();

        builder.HasOne(s => s.Asset).WithMany().HasForeignKey(s => s.AssetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(s => s.AssetVersion)
            .WithMany()
            .HasForeignKey(s => new { s.AssetId, s.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(s => s.CodeAnalysisReportHeader)
            .WithMany()
            .HasForeignKey(s => new { s.AssetId, s.AssetVersionId, s.CodeAnalysisReportHeaderId })
            .HasPrincipalKey(h => new { h.AssetId, h.AssetVersionId, h.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(s => s.ModerationSubmission)
            .WithMany()
            .HasForeignKey(s => new { s.AssetId, s.AssetVersionId, s.ModerationSubmissionId })
            .HasPrincipalKey(m => new { m.AssetId, m.AssetVersionId, m.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.ModerationSubmissionId).IsUnique();
    }
}
