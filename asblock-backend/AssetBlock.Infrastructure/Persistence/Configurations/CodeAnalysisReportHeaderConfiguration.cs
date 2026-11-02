using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
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
            table.HasCheckConstraint(
                "CK_code_analysis_report_headers_python_report_id",
                "\"PythonReportId\" IS NULL OR \"PythonReportId\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "CK_code_analysis_report_headers_report_digest",
                "\"ReportDigest\" IS NULL OR \"ReportDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "CK_code_analysis_report_headers_selection_generation",
                "\"SelectionGeneration\" IS NULL OR \"SelectionGeneration\" > 0");
            table.HasCheckConstraint(
                "CK_code_analysis_report_headers_coverage",
                "\"CoverageLevel\" IS NULL OR \"CoverageLevel\" IN ('FULL', 'PARTIAL', 'UNSUPPORTED')");
            table.HasCheckConstraint(
                "CK_code_analysis_report_headers_disposition",
                "\"Disposition\" IS NULL OR \"Disposition\" IN ('SELECTED', 'HISTORICAL_SUPERSEDED', 'HISTORICAL_STALE_INPUT', 'HISTORICAL_INCOMPLETE')");
            table.HasCheckConstraint(
                "CK_code_analysis_report_headers_counts",
                "(\"AnalyzedFileCount\" IS NULL OR \"AnalyzedFileCount\" >= 0) AND (\"OmissionCount\" IS NULL OR \"OmissionCount\" >= 0) AND (\"FindingCount\" IS NULL OR \"FindingCount\" >= 0)");
        });

        builder.Property(f => f.RetrievalSummaryJson).HasColumnType("jsonb");

        builder.HasKey(h => h.Id);
        builder.Property(h => h.ContentSha256).IsRequired().HasColumnType("char(64)");
        builder.Property(h => h.PolicyVersion).IsRequired().HasMaxLength(64);
        builder.Property(h => h.Purpose)
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();

        builder.Property(h => h.PythonReportId).HasColumnType("char(64)").IsFixedLength();
        builder.Property(h => h.ReportDigest).HasColumnType("char(64)").IsFixedLength();
        builder.Property(h => h.CoverageLevel)
            .HasMaxLength(16)
            .HasConversion(s => s.ToString(), s => Enum.Parse<CodeAnalysisCoverageLevel>(s!));
        builder.Property(h => h.Disposition)
            .HasMaxLength(32)
            .HasConversion(s => s.ToString(), s => Enum.Parse<CodeAnalysisReportDisposition>(s!));

        builder.HasOne(h => h.Asset).WithMany().HasForeignKey(h => h.AssetId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(h => h.AssetVersion)
            .WithMany()
            .HasForeignKey(h => new { h.AssetId, h.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CodeAnalysisRun>().WithMany().HasForeignKey(h => h.AnalysisRunId).OnDelete(DeleteBehavior.Restrict);

        // Exactly one finalized report per run.
        builder.HasIndex(h => h.AnalysisRunId)
            .IsUnique()
            .HasFilter("\"AnalysisRunId\" IS NOT NULL AND \"IsFinalized\"")
            .HasDatabaseName("UIX_code_analysis_report_headers_finalized_run");

        // The analysis service owns report identity; the internal id stays separate from it.
        builder.HasIndex(h => h.PythonReportId)
            .IsUnique()
            .HasFilter("\"PythonReportId\" IS NOT NULL")
            .HasDatabaseName("UIX_code_analysis_report_headers_python_report_id");

        builder.HasIndex(h => new { h.AssetId, h.AssetVersionId })
            .HasDatabaseName("IX_code_analysis_report_headers_version");
    }
}
