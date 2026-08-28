using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddOutboxDeliveryAndDeadLetterReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_dispatch",
                table: "outbox_messages");

            migrationBuilder.AddColumn<string>(
                name: "DeadLetterReason",
                table: "outbox_messages",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeadLetteredAt",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastReplayedAt",
                table: "outbox_messages",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ReplayCount",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                table: "outbox_messages",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "outbox_email_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OutboxMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RecipientAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateKind = table.Column<int>(type: "integer", nullable: false),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_email_deliveries", x => x.Id);
                });

            migrationBuilder.Sql("""
                UPDATE outbox_messages
                SET "Status" = 1
                WHERE "ProcessedAt" IS NOT NULL;

                UPDATE outbox_messages
                SET "Status" = 2,
                    "DeadLetteredAt" = COALESCE("OccurredAt", NOW()),
                    "DeadLetterReason" = CASE 
                        WHEN "LastError" LIKE 'DEAD_LETTER:%' THEN SUBSTRING(LTRIM(SUBSTRING("LastError" FROM 13)) FROM 1 FOR 2000)
                        ELSE SUBSTRING(COALESCE("LastError", 'Exceeded maximum retry attempts') FROM 1 FOR 2000)
                    END
                WHERE "ProcessedAt" IS NULL
                  AND ("AttemptCount" >= 10 OR "LastError" LIKE 'DEAD_LETTER:%');
                """);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_dead_letter",
                table: "outbox_messages",
                columns: new[] { "Status", "DeadLetteredAt", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_dispatch",
                table: "outbox_messages",
                columns: new[] { "Status", "NextAttemptAt", "LockedUntil", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_email_deliveries_MessageId",
                table: "outbox_email_deliveries",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_email_deliveries_OutboxMessageId",
                table: "outbox_email_deliveries",
                column: "OutboxMessageId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "outbox_email_deliveries");

            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_dead_letter",
                table: "outbox_messages");

            migrationBuilder.DropIndex(
                name: "IX_outbox_messages_dispatch",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "DeadLetterReason",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "DeadLetteredAt",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "LastReplayedAt",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "ReplayCount",
                table: "outbox_messages");

            migrationBuilder.DropColumn(
                name: "Status",
                table: "outbox_messages");

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_dispatch",
                table: "outbox_messages",
                columns: new[] { "ProcessedAt", "NextAttemptAt", "LockedUntil", "OccurredAt" });
        }
    }
}
