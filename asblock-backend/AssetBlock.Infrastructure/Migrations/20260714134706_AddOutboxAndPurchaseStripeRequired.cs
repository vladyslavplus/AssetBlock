using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxAndPurchaseStripeRequired : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SourceOutboxMessageId",
                table: "user_notifications",
                type: "uuid",
                nullable: true);

            // Unique legacy IDs so NOT NULL + unique StripePaymentId succeed for historical null/blank rows.
            migrationBuilder.Sql(
                """
                UPDATE purchases
                SET "StripePaymentId" = 'legacy-' || "Id"::text
                WHERE "StripePaymentId" IS NULL OR btrim("StripePaymentId") = '';
                """);

            migrationBuilder.AlterColumn<string>(
                name: "StripePaymentId",
                table: "purchases",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256,
                oldNullable: true);

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_SourceOutboxMessageId",
                table: "user_notifications",
                column: "SourceOutboxMessageId",
                unique: true,
                filter: "\"SourceOutboxMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_dispatch",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "NextAttemptAt", "LockedUntil", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "IX_user_notifications_SourceOutboxMessageId",
                table: "user_notifications");

            migrationBuilder.DropColumn(
                name: "SourceOutboxMessageId",
                table: "user_notifications");

            migrationBuilder.AlterColumn<string>(
                name: "StripePaymentId",
                table: "purchases",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);
        }
    }
}
