using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CodeAnalysisReportStageConfiguration : IEntityTypeConfiguration<CodeAnalysisReportStage>
{
    public void Configure(EntityTypeBuilder<CodeAnalysisReportStage> builder)
    {
        builder.ToTable("code_analysis_report_stages", table =>
        {
            table.HasCheckConstraint("CK_code_analysis_report_stages_stage", "\"Stage\" ~ '^[A-Z][A-Z0-9_]{0,31}$'");
            table.HasCheckConstraint(
                "CK_code_analysis_report_stages_applicability",
                "\"Applicability\" IN ('MANDATORY', 'OPTIONAL')");
            table.HasCheckConstraint(
                "CK_code_analysis_report_stages_outcome",
                "\"Outcome\" IN ('SUCCEEDED', 'FAILED', 'NOT_APPLICABLE')");
            table.HasCheckConstraint("CK_code_analysis_report_stages_duration", "\"DurationMilliseconds\" >= 0");
            table.HasCheckConstraint(
                "CK_code_analysis_report_stages_input_sha",
                "\"InputSha256\" IS NULL OR \"InputSha256\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint(
                "CK_code_analysis_report_stages_output_sha",
                "\"OutputSha256\" IS NULL OR \"OutputSha256\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_report_stages_tool_identity", "jsonb_typeof(\"ToolIdentityJson\") = 'object'");
            table.HasCheckConstraint("CK_code_analysis_report_stages_counts", "jsonb_typeof(\"CountsJson\") = 'object'");
        });

        builder.HasKey(s => s.Id);

        builder.Property(s => s.Stage).IsRequired().HasMaxLength(32);
        builder.Property(s => s.Applicability)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(s => s.ToString(), s => Enum.Parse<CodeAnalysisStageApplicability>(s));
        builder.Property(s => s.Outcome)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(s => s.ToString(), s => Enum.Parse<CodeAnalysisStageOutcome>(s));
        builder.Property(s => s.ErrorCode).HasMaxLength(64);
        builder.Property(s => s.Retryable).IsRequired();
        builder.Property(s => s.DurationMilliseconds).IsRequired();
        builder.Property(s => s.InputSha256).HasColumnType("char(64)").IsFixedLength();
        builder.Property(s => s.OutputSha256).HasColumnType("char(64)").IsFixedLength();
        builder.Property(s => s.ToolIdentityJson).IsRequired().HasColumnType("jsonb");
        builder.Property(s => s.CountsJson).IsRequired().HasColumnType("jsonb");
        builder.Property(s => s.CreatedAt).IsRequired();

        builder.HasOne<CodeAnalysisReportHeader>().WithMany().HasForeignKey(s => s.ReportId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CodeAnalysisRun>().WithMany().HasForeignKey(s => s.ReuseParentRunId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<CodeAnalysisReportStage>().WithMany().HasForeignKey(s => s.ReuseParentStageId).OnDelete(DeleteBehavior.Restrict);

        // Exactly one record per expected stage of a report.
        builder.HasIndex(s => new { s.ReportId, s.Stage })
            .IsUnique()
            .HasDatabaseName("UIX_code_analysis_report_stages_report_stage");
    }
}
