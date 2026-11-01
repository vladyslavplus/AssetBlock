using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddReviewAppeals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AppealOfDecisionId",
                table: "moderation_submissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AppealRationale",
                table: "moderation_submissions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                table: "moderation_submissions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "STANDARD");

            migrationBuilder.CreateIndex(
                name: "UIX_moderation_submissions_appeal_decision",
                table: "moderation_submissions",
                column: "AppealOfDecisionId",
                unique: true,
                filter: "\"AppealOfDecisionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_moderation_submissions_appeal",
                table: "moderation_submissions",
                sql: "(\"Kind\" = 'STANDARD' AND \"AppealOfDecisionId\" IS NULL AND \"AppealRationale\" IS NULL) OR (\"Kind\" = 'APPEAL' AND \"AppealOfDecisionId\" IS NOT NULL AND \"AppealRationale\" IS NOT NULL AND length(trim(\"AppealRationale\")) BETWEEN 1 AND 2000)");

            migrationBuilder.AddForeignKey(
                name: "FK_moderation_submissions_moderation_decisions_AppealOfDecisio~",
                table: "moderation_submissions",
                column: "AppealOfDecisionId",
                principalTable: "moderation_decisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_moderation_submissions_moderation_decisions_AppealOfDecisio~",
                table: "moderation_submissions");

            migrationBuilder.DropIndex(
                name: "UIX_moderation_submissions_appeal_decision",
                table: "moderation_submissions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_moderation_submissions_appeal",
                table: "moderation_submissions");

            migrationBuilder.DropColumn(
                name: "AppealOfDecisionId",
                table: "moderation_submissions");

            migrationBuilder.DropColumn(
                name: "AppealRationale",
                table: "moderation_submissions");

            migrationBuilder.DropColumn(
                name: "Kind",
                table: "moderation_submissions");
        }
    }
}
