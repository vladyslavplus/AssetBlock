using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class SellerResponseConfiguration : IEntityTypeConfiguration<SellerResponse>
{
    public void Configure(EntityTypeBuilder<SellerResponse> builder)
    {
        builder.ToTable("seller_responses", table =>
        {
            table.HasCheckConstraint("CK_seller_responses_revision", "\"SellerEvidenceRevision\" > 0");
            table.HasCheckConstraint("CK_seller_responses_scope", "\"AssetVersionId\" = \"WorkspaceVersionScopeKey\"");
            table.HasCheckConstraint("CK_seller_responses_type", "\"ResponseType\" IN ('PRIOR_WORK','PERMITTED_COMPONENT','INDEPENDENT_IMPLEMENTATION','WRONG_SOURCE','CORRECTED_FILES','NO_EVIDENCE')");
            table.HasCheckConstraint("CK_seller_responses_text", $"char_length(\"Text\") <= {SellerEvidenceLimits.TEXT_MAX_LENGTH} AND btrim(\"Text\") = \"Text\" AND (\"ResponseType\" = 'NO_EVIDENCE' OR char_length(\"Text\") > 0)");
        });
        builder.HasKey(r => r.Id);
        builder.Property(r => r.ResponseType).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(r => r.Text).HasMaxLength(SellerEvidenceLimits.TEXT_MAX_LENGTH).IsRequired();
        builder.HasOne<AssetVersion>().WithMany().HasForeignKey(r => new { r.AssetId, r.AssetVersionId }).HasPrincipalKey(v => new { v.AssetId, v.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AssetDraftWorkspace>().WithMany().HasForeignKey(r => new { r.AssetId, r.WorkspaceId, r.WorkspaceVersionScopeKey }).HasPrincipalKey(w => new { w.AssetId, w.Id, w.WorkspaceVersionScopeKey }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CodeAnalysisReportHeader>().WithMany().HasForeignKey(r => new { r.AssetId, r.AssetVersionId, r.ReportId }).HasPrincipalKey(h => new { h.AssetId, h.AssetVersionId, h.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CodeAnalysisReportFinding>().WithMany().HasForeignKey(r => new { r.ReportId, r.FindingId }).HasPrincipalKey(f => new { f.ReportId, f.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<AssetSellerEvidenceRevision>().WithMany().HasForeignKey(r => new { r.WorkspaceId, Revision = r.SellerEvidenceRevision }).HasPrincipalKey(e => new { e.WorkspaceId, e.Revision }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(r => r.ActorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(r => new { r.WorkspaceId, r.SellerEvidenceRevision, r.FindingId }).IsUnique();
        builder.HasIndex(r => new { r.AssetVersionId, r.SellerEvidenceRevision, r.Id });
    }
}
