using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddModerationAssignment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ClaimHolderId",
                table: "moderation_submissions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClaimedAt",
                table: "moderation_submissions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_ClaimHolderId",
                table: "moderation_submissions",
                column: "ClaimHolderId");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_queue",
                table: "moderation_submissions",
                columns: new[] { "CreatedAt", "Id" },
                filter: "\"State\" IN ('SUBMITTED', 'IN_REVIEW')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_moderation_submissions_claim",
                table: "moderation_submissions",
                sql: "(\"State\" = 'IN_REVIEW' AND \"ClaimHolderId\" IS NOT NULL AND \"ClaimedAt\" IS NOT NULL) OR (\"State\" <> 'IN_REVIEW' AND \"ClaimHolderId\" IS NULL AND \"ClaimedAt\" IS NULL)");

            migrationBuilder.AddForeignKey(
                name: "FK_moderation_submissions_users_ClaimHolderId",
                table: "moderation_submissions",
                column: "ClaimHolderId",
                principalTable: "users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_moderation_submissions_users_ClaimHolderId",
                table: "moderation_submissions");

            migrationBuilder.DropIndex(
                name: "IX_moderation_submissions_ClaimHolderId",
                table: "moderation_submissions");

            migrationBuilder.DropIndex(
                name: "IX_moderation_submissions_queue",
                table: "moderation_submissions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_moderation_submissions_claim",
                table: "moderation_submissions");

            migrationBuilder.DropColumn(
                name: "ClaimHolderId",
                table: "moderation_submissions");

            migrationBuilder.DropColumn(
                name: "ClaimedAt",
                table: "moderation_submissions");
        }
    }
}
