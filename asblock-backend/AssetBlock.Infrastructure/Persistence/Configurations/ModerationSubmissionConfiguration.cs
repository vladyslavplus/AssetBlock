using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class ModerationSubmissionConfiguration : IEntityTypeConfiguration<ModerationSubmission>
{
    private const string UNIQUE_ACTIVE_CASE = "UIX_moderation_submissions_active_case";

    public void Configure(EntityTypeBuilder<ModerationSubmission> builder)
    {
        builder.ToTable("moderation_submissions", table =>
        {
            table.HasCheckConstraint("CK_moderation_submissions_case_revision", "\"CaseRevision\" > 0");
            table.HasCheckConstraint("CK_moderation_submissions_evidence_digest", "length(\"SellerEvidenceDigest\") = 64");
            table.HasCheckConstraint("CK_moderation_submissions_content_sha", "length(\"ContentSha256\") = 64");
            table.HasCheckConstraint(
                "CK_moderation_submissions_workspace_scope",
                "\"AssetVersionId\" = \"WorkspaceVersionScopeKey\"");
        });

        builder.HasKey(s => s.Id);
        builder.Property(s => s.ContentSha256).IsRequired().HasColumnType("char(64)");
        builder.Property(s => s.SellerEvidenceDigest).IsRequired().HasColumnType("char(64)");
        builder.Property(s => s.PolicyVersion).IsRequired().HasMaxLength(64);
        builder.Property(s => s.WorkspaceVersionScopeKey).IsRequired();
        builder.Property(s => s.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(s => s.WithdrawalReason).HasConversion<string>().HasMaxLength(32);

        builder.HasOne(s => s.Asset).WithMany().HasForeignKey(s => s.AssetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(s => s.AssetVersion)
            .WithMany()
            .HasForeignKey(s => new { s.AssetId, s.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(s => s.Workspace)
            .WithMany()
            .HasForeignKey(s => new { s.AssetId, s.WorkspaceId, s.WorkspaceVersionScopeKey })
            .HasPrincipalKey(w => new { w.AssetId, w.Id, w.WorkspaceVersionScopeKey })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(s => s.CodeAnalysisReportHeader)
            .WithMany()
            .HasForeignKey(s => new { s.AssetId, s.AssetVersionId, s.CodeAnalysisReportHeaderId })
            .HasPrincipalKey(h => new { h.AssetId, h.AssetVersionId, h.Id })
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(s => s.PreviousSubmission)
            .WithMany()
            .HasForeignKey(s => new { s.AssetId, s.AssetVersionId, s.PreviousSubmissionId })
            .HasPrincipalKey(p => new { p.AssetId, p.AssetVersionId, p.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.AssetVersionId)
            .IsUnique()
            .HasFilter("\"State\" IN ('SUBMITTED', 'IN_REVIEW')")
            .HasDatabaseName(UNIQUE_ACTIVE_CASE);
    }
}
