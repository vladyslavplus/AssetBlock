using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSellerAnalysisFindingDetails : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ParentAnalysisRunId",
                table: "security_rechecks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ForeignOrigin",
                table: "code_analysis_report_findings",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OwnExcerpt",
                table: "code_analysis_report_findings",
                type: "character varying(320)",
                maxLength: 320,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_findings_origin",
                table: "code_analysis_report_findings",
                sql: "\"ForeignOrigin\" IS NULL OR \"ForeignOrigin\" IN ('OWN', 'FOREIGN')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_findings_origin",
                table: "code_analysis_report_findings");

            migrationBuilder.DropColumn(
                name: "ParentAnalysisRunId",
                table: "security_rechecks");

            migrationBuilder.DropColumn(
                name: "ForeignOrigin",
                table: "code_analysis_report_findings");

            migrationBuilder.DropColumn(
                name: "OwnExcerpt",
                table: "code_analysis_report_findings");
        }
    }
}
