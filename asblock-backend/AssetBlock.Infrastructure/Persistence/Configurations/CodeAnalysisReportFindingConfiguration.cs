using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CodeAnalysisReportFindingConfiguration : IEntityTypeConfiguration<CodeAnalysisReportFinding>
{
    public void Configure(EntityTypeBuilder<CodeAnalysisReportFinding> builder)
    {
        builder.ToTable("code_analysis_report_findings", table =>
        {
            table.HasCheckConstraint("CK_code_analysis_report_findings_key", "\"FindingKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
            table.HasCheckConstraint("CK_code_analysis_report_findings_spans", "(\"OwnStartByte\" IS NULL AND \"OwnEndByte\" IS NULL) OR (\"OwnStartByte\" IS NOT NULL AND \"OwnEndByte\" IS NOT NULL AND \"OwnStartByte\" >= 0 AND \"OwnEndByte\" >= \"OwnStartByte\")");
            table.HasCheckConstraint("CK_code_analysis_report_findings_lines", "(\"OwnStartLine\" IS NULL AND \"OwnEndLine\" IS NULL) OR (\"OwnStartLine\" IS NOT NULL AND \"OwnEndLine\" IS NOT NULL AND \"OwnStartLine\" > 0 AND \"OwnEndLine\" >= \"OwnStartLine\")");
            table.HasCheckConstraint(
                "CK_code_analysis_report_findings_sensitivity",
                "\"ForeignSensitivity\" IN ('PUBLIC', 'PRIVATE')");
            table.HasCheckConstraint(
                "CK_code_analysis_report_findings_origin",
                "\"ForeignOrigin\" IS NULL OR \"ForeignOrigin\" IN ('OWN', 'FOREIGN')");
            // Private references must not carry any attribution payload.
            table.HasCheckConstraint(
                "CK_code_analysis_report_findings_attribution",
                "\"ForeignSensitivity\" <> 'PRIVATE' OR \"PublicAttributionJson\" IS NULL");
            table.HasCheckConstraint(
                "CK_code_analysis_report_findings_attribution_json",
                "\"PublicAttributionJson\" IS NULL OR jsonb_typeof(\"PublicAttributionJson\") = 'object'");
            // PostgreSQL float ordering puts NaN above every value, so this rejects exactly the
            // non-finite case while leaving every ordinary score untouched.
            table.HasCheckConstraint("CK_code_analysis_report_findings_scores", "\"TokenScore\" IS NULL OR \"TokenScore\" <> 'NaN'::double precision");
            table.HasCheckConstraint("CK_code_analysis_report_findings_scores_semantic", "\"SemanticScore\" IS NULL OR \"SemanticScore\" <> 'NaN'::double precision");
        });

        builder.Property(f => f.RetrievalEvidenceJson).HasColumnType("jsonb");

        builder.HasKey(f => f.Id);

        builder.Property(f => f.FindingKey).IsRequired().HasMaxLength(128);
        builder.Property(f => f.FindingType).IsRequired().HasMaxLength(48);
        builder.Property(f => f.OwnScopePath).IsRequired().HasMaxLength(1024);
        builder.Property(f => f.OwnExcerpt).IsRequired().HasMaxLength(320);
        builder.Property(f => f.OwnStartByte).IsRequired(false);
        builder.Property(f => f.OwnEndByte).IsRequired(false);
        builder.Property(f => f.OwnStartLine).IsRequired(false);
        builder.Property(f => f.OwnEndLine).IsRequired(false);
        builder.Property(f => f.ForeignSourceId).HasMaxLength(128);
        builder.Property(f => f.ForeignOrigin).HasMaxLength(16);
        builder.Property(f => f.ForeignSensitivity)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(s => s.ToString(), s => Enum.Parse<CodeAnalysisFindingSensitivity>(s));
        builder.Property(f => f.PublicAttributionJson).HasColumnType("jsonb");
        builder.Property(f => f.ReasonText).IsRequired().HasMaxLength(1024);
        builder.Property(f => f.RequestedAction).IsRequired().HasMaxLength(512);
        builder.Property(f => f.CreatedAt).IsRequired();

        builder.HasOne<CodeAnalysisReportHeader>().WithMany().HasForeignKey(f => f.ReportId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(f => new { f.ReportId, f.FindingKey })
            .IsUnique()
            .HasDatabaseName("UIX_code_analysis_report_findings_report_key");
    }
}
