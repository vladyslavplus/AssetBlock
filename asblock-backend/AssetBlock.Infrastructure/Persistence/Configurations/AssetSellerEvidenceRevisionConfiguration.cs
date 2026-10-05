using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AssetSellerEvidenceRevisionConfiguration : IEntityTypeConfiguration<AssetSellerEvidenceRevision>
{
    private static readonly string _revisionScopeCheck = $"""
        (("AssetVersionId" IS NULL AND "WorkspaceVersionScopeKey" = '{DraftWorkspaceVersionScopes.PreUpload:D}'::uuid)
        OR ("AssetVersionId" IS NOT NULL AND "WorkspaceVersionScopeKey" = "AssetVersionId"))
        """;

    public void Configure(EntityTypeBuilder<AssetSellerEvidenceRevision> builder)
    {
        builder.ToTable("asset_seller_evidence_revisions", table =>
        {
            table.HasCheckConstraint("CK_asset_seller_evidence_revisions_revision", "\"Revision\" > 0");
            table.HasCheckConstraint("CK_asset_seller_evidence_revisions_digest", "length(\"ContentDigest\") = 64");
            table.HasCheckConstraint("CK_asset_seller_evidence_revisions_revision_scope", _revisionScopeCheck);
        });

        builder.HasKey(r => r.Id);
        builder.Property(r => r.ContentDigest).IsRequired().HasColumnType("char(64)");
        builder.Property(r => r.PayloadJson).HasColumnType("jsonb").IsRequired();
        builder.Property(r => r.WorkspaceVersionScopeKey).IsRequired();

        builder.HasOne(r => r.Asset).WithMany().HasForeignKey(r => r.AssetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.Workspace)
            .WithMany()
            .HasForeignKey(r => new { r.AssetId, r.WorkspaceId, r.WorkspaceVersionScopeKey })
            .HasPrincipalKey(w => new { w.AssetId, w.Id, w.WorkspaceVersionScopeKey })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(r => r.AssetVersion)
            .WithMany()
            .HasForeignKey(r => new { r.AssetId, r.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(r => new { r.WorkspaceId, r.Revision }).IsUnique();
    }
}
