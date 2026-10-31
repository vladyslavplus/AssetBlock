using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRetrievalReviewEvidence : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "RetrievalSummaryJson",
                table: "code_analysis_report_headers",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RetrievalEvidenceJson",
                table: "code_analysis_report_findings",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "RetrievalSummaryJson",
                table: "code_analysis_report_headers");

            migrationBuilder.DropColumn(
                name: "RetrievalEvidenceJson",
                table: "code_analysis_report_findings");
        }
    }
}
