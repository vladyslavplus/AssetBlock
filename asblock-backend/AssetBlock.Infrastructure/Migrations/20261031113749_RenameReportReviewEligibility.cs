using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameReportReviewEligibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_admissions_authority_reason",
                table: "code_analysis_report_admissions");

            migrationBuilder.RenameColumn(
                name: "CanAuthorizePublication",
                table: "code_analysis_report_headers",
                newName: "ReviewEligible");

            migrationBuilder.RenameColumn(
                name: "PublicationAuthorityReason",
                table: "code_analysis_report_admissions",
                newName: "ReviewEligibilityReason");

            migrationBuilder.RenameColumn(
                name: "CanAuthorizePublication",
                table: "code_analysis_report_admissions",
                newName: "ReviewEligible");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_admissions_review_reason",
                table: "code_analysis_report_admissions",
                sql: "\"ReviewEligibilityReason\" IS NOT NULL AND length(\"ReviewEligibilityReason\") > 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_admissions_review_reason",
                table: "code_analysis_report_admissions");

            migrationBuilder.RenameColumn(
                name: "ReviewEligible",
                table: "code_analysis_report_headers",
                newName: "CanAuthorizePublication");

            migrationBuilder.RenameColumn(
                name: "ReviewEligible",
                table: "code_analysis_report_admissions",
                newName: "CanAuthorizePublication");

            migrationBuilder.RenameColumn(
                name: "ReviewEligibilityReason",
                table: "code_analysis_report_admissions",
                newName: "PublicationAuthorityReason");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_admissions_authority_reason",
                table: "code_analysis_report_admissions",
                sql: "\"PublicationAuthorityReason\" IS NOT NULL AND length(\"PublicationAuthorityReason\") > 0");
        }
    }
}
