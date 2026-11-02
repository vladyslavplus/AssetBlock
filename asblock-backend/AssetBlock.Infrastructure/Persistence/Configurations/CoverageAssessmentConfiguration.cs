using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CoverageAssessmentConfiguration : IEntityTypeConfiguration<CoverageAssessment>
{
    public void Configure(EntityTypeBuilder<CoverageAssessment> b)
    {
        b.ToTable("coverage_assessments", t =>
        {
            t.HasCheckConstraint("CK_coverage_assessments_outcome", "\"Outcome\" IN ('ACCEPTED','BLOCKED')");
            t.HasCheckConstraint("CK_coverage_assessments_text", "length(\"Rationale\") BETWEEN 1 AND 2000 AND length(\"ReviewPlan\") BETWEEN 1 AND 2000 AND length(\"CompletedPlanReference\") <= 200");
            t.HasCheckConstraint("CK_coverage_assessments_plan", "\"Outcome\" <> 'ACCEPTED' OR (\"ManageableScope\" = TRUE AND length(\"CompletedPlanReference\") > 0)");
        });
        b.HasKey(a => a.Id);
        b.Property(a => a.Outcome).HasConversion<string>().HasMaxLength(16);
        b.Property(a => a.CompetenceJson).HasColumnType("jsonb").IsRequired();
        b.Property(a => a.ScopeJson).HasColumnType("jsonb").IsRequired();
        b.Property(a => a.Rationale).HasMaxLength(2000).IsRequired();
        b.Property(a => a.ReviewPlan).HasMaxLength(2000).IsRequired();
        b.Property(a => a.CompletedPlanReference).HasMaxLength(200).IsRequired();
        b.HasOne<CoverageReviewRequest>().WithMany().HasForeignKey(a => a.RequestId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(a => a.ModeratorUserId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(a => a.RequestId).IsUnique();
    }
}
