using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddRecommendationPersonalization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "user_recommendation_preferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPersonalized = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    OptedInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_recommendation_preferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_user_recommendation_preferences_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_source_click_affinity",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Clicks = table.Column<long>(type: "bigint", nullable: false),
                    LastClickedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_source_click_affinity", x => new { x.UserId, x.SourceAssetId, x.TargetAssetId });
                    table.CheckConstraint("CK_user_source_click_affinity_clicks_non_negative", "\"Clicks\" >= 0");
                    table.ForeignKey(
                        name: "FK_user_source_click_affinity_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tag_affinity",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purchases = table.Column<long>(type: "bigint", nullable: false),
                    Reviews = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_tag_affinity", x => new { x.UserId, x.TagId });
                    table.CheckConstraint("CK_user_tag_affinity_counters_non_negative", "\"Purchases\" >= 0 AND \"Reviews\" >= 0");
                    table.ForeignKey(
                        name: "FK_user_tag_affinity_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_events_ActorUserId_EventType_OccurredAt",
                table: "recommendation_events",
                columns: new[] { "ActorUserId", "EventType", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_recommendation_preferences");

            migrationBuilder.DropTable(
                name: "user_source_click_affinity");

            migrationBuilder.DropTable(
                name: "user_tag_affinity");

            migrationBuilder.DropIndex(
                name: "IX_recommendation_events_ActorUserId_EventType_OccurredAt",
                table: "recommendation_events");
        }
    }
}
