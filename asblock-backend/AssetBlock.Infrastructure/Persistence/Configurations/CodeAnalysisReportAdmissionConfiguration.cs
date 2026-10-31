using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CodeAnalysisReportAdmissionConfiguration : IEntityTypeConfiguration<CodeAnalysisReportAdmission>
{
    public void Configure(EntityTypeBuilder<CodeAnalysisReportAdmission> builder)
    {
        builder.ToTable("code_analysis_report_admissions", table =>
        {
            table.HasCheckConstraint("CK_code_analysis_report_admissions_kind", "\"AdmissionKind\" = 'SERVER_VALIDATED'");
            table.HasCheckConstraint("CK_code_analysis_report_admissions_report_digest", "\"ReportDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_report_admissions_python_report_id", "\"PythonReportId\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_report_admissions_input_digest", "\"InputDigest\" ~ '^[0-9a-f]{64}$'");
            table.HasCheckConstraint("CK_code_analysis_report_admissions_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
            table.HasCheckConstraint("CK_code_analysis_report_admissions_slot_number", "\"ReservationSlotNumber\" >= 0");
            table.HasCheckConstraint("CK_code_analysis_report_admissions_generation", "\"ReservationGeneration\" > 0");
            table.HasCheckConstraint("CK_code_analysis_report_admissions_lease_token_digest", "\"ReservationLeaseTokenDigest\" ~ '^[0-9a-f]{64}$'");
            // Review eligibility is a server decision recorded with an explicit reason.
            table.HasCheckConstraint(
                "CK_code_analysis_report_admissions_review_reason",
                "\"ReviewEligibilityReason\" IS NOT NULL AND length(\"ReviewEligibilityReason\") > 0");
        });

        builder.HasKey(a => a.Id);

        builder.Property(a => a.AdmissionKind)
            .IsRequired()
            .HasMaxLength(32)
            .HasConversion(s => s.ToString(), s => Enum.Parse<CodeAnalysisAdmissionKind>(s));
        builder.Property(a => a.AdmittedBy).IsRequired().HasMaxLength(64);
        builder.Property(a => a.AdmittedAt).IsRequired();
        builder.Property(a => a.ReportDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(a => a.PythonReportId).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(a => a.InputDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(a => a.ServiceKey).IsRequired().HasMaxLength(128);
        builder.Property(a => a.ReservationLeaseTokenDigest).IsRequired().HasColumnType("char(64)").IsFixedLength();
        builder.Property(a => a.ReviewEligibilityReason).IsRequired().HasMaxLength(512);
        builder.Property(a => a.CreatedAt).IsRequired();

        // One server admission per admitted report; its absence is what makes a header untrusted.
        builder.HasOne<CodeAnalysisReportHeader>().WithMany().HasForeignKey(a => a.ReportId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => a.ReportId)
            .IsUnique()
            .HasDatabaseName("UIX_code_analysis_report_admissions_report");
    }
}
