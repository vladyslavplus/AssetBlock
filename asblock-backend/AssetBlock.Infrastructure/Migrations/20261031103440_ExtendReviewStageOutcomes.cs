using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ExtendReviewStageOutcomes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_stages_outcome",
                table: "code_analysis_report_stages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_stages_outcome",
                table: "code_analysis_report_stages",
                sql: "\"Outcome\" IN ('SUCCEEDED', 'FAILED', 'NOT_APPLICABLE')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_stages_outcome",
                table: "code_analysis_report_stages");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_stages_outcome",
                table: "code_analysis_report_stages",
                sql: "\"Outcome\" IN ('SUCCEEDED', 'FAILED')");
        }
    }
}
