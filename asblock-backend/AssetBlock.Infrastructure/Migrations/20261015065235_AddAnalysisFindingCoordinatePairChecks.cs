using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAnalysisFindingCoordinatePairChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_findings_spans",
                table: "code_analysis_report_findings");

            migrationBuilder.AlterColumn<string>(
                name: "RequestedAction",
                table: "code_analysis_report_findings",
                type: "character varying(512)",
                maxLength: 512,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.AlterColumn<int>(
                name: "OwnStartByte",
                table: "code_analysis_report_findings",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AlterColumn<int>(
                name: "OwnEndByte",
                table: "code_analysis_report_findings",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "OwnEndLine",
                table: "code_analysis_report_findings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "OwnStartLine",
                table: "code_analysis_report_findings",
                type: "integer",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_findings_lines",
                table: "code_analysis_report_findings",
                sql: "(\"OwnStartLine\" IS NULL AND \"OwnEndLine\" IS NULL) OR (\"OwnStartLine\" IS NOT NULL AND \"OwnEndLine\" IS NOT NULL AND \"OwnStartLine\" > 0 AND \"OwnEndLine\" >= \"OwnStartLine\")");

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_findings_spans",
                table: "code_analysis_report_findings",
                sql: "(\"OwnStartByte\" IS NULL AND \"OwnEndByte\" IS NULL) OR (\"OwnStartByte\" IS NOT NULL AND \"OwnEndByte\" IS NOT NULL AND \"OwnStartByte\" >= 0 AND \"OwnEndByte\" >= \"OwnStartByte\")");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_findings_lines",
                table: "code_analysis_report_findings");

            migrationBuilder.DropCheckConstraint(
                name: "CK_code_analysis_report_findings_spans",
                table: "code_analysis_report_findings");

            migrationBuilder.DropColumn(
                name: "OwnEndLine",
                table: "code_analysis_report_findings");

            migrationBuilder.DropColumn(
                name: "OwnStartLine",
                table: "code_analysis_report_findings");

            migrationBuilder.AlterColumn<string>(
                name: "RequestedAction",
                table: "code_analysis_report_findings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(512)",
                oldMaxLength: 512);

            migrationBuilder.AlterColumn<int>(
                name: "OwnStartByte",
                table: "code_analysis_report_findings",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AlterColumn<int>(
                name: "OwnEndByte",
                table: "code_analysis_report_findings",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_code_analysis_report_findings_spans",
                table: "code_analysis_report_findings",
                sql: "\"OwnStartByte\" >= 0 AND \"OwnEndByte\" >= \"OwnStartByte\"");
        }
    }
}
