using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CoverageReviewRequestConfiguration : IEntityTypeConfiguration<CoverageReviewRequest>
{
    public void Configure(EntityTypeBuilder<CoverageReviewRequest> b)
    {
        b.ToTable("coverage_review_requests", t =>
        {
            t.HasCheckConstraint("CK_coverage_requests_revision", "\"RequestedWorkspaceRevision\" > 0 AND \"Revision\" > 0 AND \"DeclarationRevision\" > 0 AND \"MaterialRevision\" > 0 AND \"SellerEvidenceRevision\" >= 0");
            t.HasCheckConstraint("CK_coverage_requests_digests", "length(\"TupleDigest\") = 64 AND length(\"ContentSha256\") = 64 AND length(\"PolicyDigest\") = 64");
        });
        b.HasKey(r => r.Id);
        b.Property(r => r.PolicyVersion).HasMaxLength(80).IsRequired();
        b.Property(r => r.ContentSha256).HasColumnType("char(64)").IsRequired();
        b.Property(r => r.PolicyDigest).HasColumnType("char(64)").IsRequired();
        b.Property(r => r.TupleDigest).HasColumnType("char(64)").IsRequired();
        b.HasOne<AssetVersion>().WithMany().HasForeignKey(r => new { r.AssetId, r.AssetVersionId }).HasPrincipalKey(v => new { v.AssetId, v.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<AssetDraftWorkspace>().WithMany().HasForeignKey(r => new { r.AssetId, r.WorkspaceId, r.AssetVersionId }).HasPrincipalKey(w => new { w.AssetId, w.Id, w.WorkspaceVersionScopeKey }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<CodeAnalysisReportHeader>().WithMany().HasForeignKey(r => new { r.AssetId, r.AssetVersionId, r.ReportId }).HasPrincipalKey(h => new { h.AssetId, h.AssetVersionId, h.Id }).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(r => r.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(r => r.TupleDigest).IsUnique().HasFilter("\"IsOpen\" = TRUE");
        b.HasIndex(r => new { r.AssetVersionId, r.RequestedWorkspaceRevision, r.Id });
    }
}
