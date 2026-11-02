using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class ModerationFindingDispositionConfiguration : IEntityTypeConfiguration<ModerationFindingDisposition>
{
    public void Configure(EntityTypeBuilder<ModerationFindingDisposition> builder)
    {
        builder.ToTable("moderation_finding_dispositions", table =>
        {
            table.HasCheckConstraint("CK_finding_dispositions_revision", "\"CaseRevision\" > 0");
            table.HasCheckConstraint("CK_finding_dispositions_type", "\"Disposition\" IN ('EXPLAINED','FALSE_MATCH','ACCEPTED_WITH_LIMITS','CHANGES_NEEDED','INSUFFICIENT_EVIDENCE')");
            table.HasCheckConstraint("CK_finding_dispositions_rationale", $"char_length(\"Rationale\") BETWEEN 1 AND {SellerEvidenceLimits.TEXT_MAX_LENGTH} AND btrim(\"Rationale\") = \"Rationale\"");
        });
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Disposition).HasConversion<string>().HasMaxLength(48).IsRequired();
        builder.Property(d => d.Rationale).HasMaxLength(SellerEvidenceLimits.TEXT_MAX_LENGTH).IsRequired();
        builder.HasOne<ModerationSubmission>().WithMany().HasForeignKey(d => new { d.AssetId, d.AssetVersionId, d.SubmissionId }).HasPrincipalKey(s => new { s.AssetId, s.AssetVersionId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CodeAnalysisReportHeader>().WithMany().HasForeignKey(d => new { d.AssetId, d.AssetVersionId, d.ReportId }).HasPrincipalKey(h => new { h.AssetId, h.AssetVersionId, h.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CodeAnalysisReportFinding>().WithMany().HasForeignKey(d => new { d.ReportId, d.FindingId }).HasPrincipalKey(f => new { f.ReportId, f.Id }).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<User>().WithMany().HasForeignKey(d => d.ModeratorUserId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(d => new { d.SubmissionId, d.FindingId, d.CaseRevision }).IsUnique();
    }
}
