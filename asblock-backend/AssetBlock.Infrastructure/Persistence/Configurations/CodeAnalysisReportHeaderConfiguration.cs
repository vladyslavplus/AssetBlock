using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CodeAnalysisReportHeaderConfiguration : IEntityTypeConfiguration<CodeAnalysisReportHeader>
{
    public void Configure(EntityTypeBuilder<CodeAnalysisReportHeader> builder)
    {
        builder.ToTable("code_analysis_report_headers", table =>
        {
            table.HasCheckConstraint("CK_code_analysis_report_headers_sha", "length(\"ContentSha256\") = 64");
        });

        builder.HasKey(h => h.Id);
        builder.Property(h => h.ContentSha256).IsRequired().HasColumnType("char(64)");
        builder.Property(h => h.PolicyVersion).IsRequired().HasMaxLength(64);
        builder.Property(h => h.Purpose)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.HasOne(h => h.Asset).WithMany().HasForeignKey(h => h.AssetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(h => h.AssetVersion)
            .WithMany()
            .HasForeignKey(h => new { h.AssetId, h.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(h => new { h.AssetId, h.AssetVersionId, h.ContentSha256, h.PolicyVersion, h.InputRevision }).IsUnique();
    }
}
