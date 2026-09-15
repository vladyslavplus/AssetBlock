using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecommendationTelemetry : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "recommendation_daily",
                columns: table => new
                {
                    DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RankingVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImpressionCount = table.Column<long>(type: "bigint", nullable: false),
                    ClickCount = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_daily", x => new { x.DayUtc, x.SourceAssetId, x.TargetAssetId, x.RankingVersion });
                    table.CheckConstraint("CK_recommendation_daily_counters_non_negative", "\"ImpressionCount\" >= 0 AND \"ClickCount\" >= 0");
                    table.CheckConstraint("CK_recommendation_daily_RankingVersion_length", "length(\"RankingVersion\") > 0\r\nAND length(\"RankingVersion\") <= 64");
                });

            migrationBuilder.CreateTable(
                name: "recommendation_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExposureId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlotPosition = table.Column<int>(type: "integer", nullable: false),
                    RankingVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VisitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_events", x => x.Id);
                    table.CheckConstraint("CK_recommendation_events_DeviceClass", "\"DeviceClass\" IN (\r\n    'MOBILE',\r\n    'TABLET',\r\n    'DESKTOP',\r\n    'UNKNOWN')");
                    table.CheckConstraint("CK_recommendation_events_EventType", "\"EventType\" IN (\r\n    'IMPRESSION',\r\n    'CLICK')");
                    table.CheckConstraint("CK_recommendation_events_RankingVersion_length", "length(\"RankingVersion\") > 0\r\nAND length(\"RankingVersion\") <= 64");
                    table.CheckConstraint("CK_recommendation_events_SlotPosition", "\"SlotPosition\" >= 0\r\nAND \"SlotPosition\" <= 11");
                });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_events_OccurredAt_brin",
                table: "recommendation_events",
                column: "OccurredAt")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_events_OccurredAt_Id",
                table: "recommendation_events",
                columns: new[] { "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_recommendation_events_ExposureId_EventType_TargetAssetId",
                table: "recommendation_events",
                columns: new[] { "ExposureId", "EventType", "TargetAssetId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "recommendation_daily");

            migrationBuilder.DropTable(
                name: "recommendation_events");
        }
    }
}
