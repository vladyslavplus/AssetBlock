using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserProfileAndSocialPlatforms : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AvatarUrl",
                table: "users",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Bio",
                table: "users",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsPublicProfile",
                table: "users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.CreateTable(
                name: "social_platforms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IconName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_social_platforms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "user_social_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_social_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_social_links_social_platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalTable: "social_platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_social_links_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.InsertData(
                table: "social_platforms",
                columns: new[] { "Id", "CreatedAt", "IconName", "Name", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("a3b9c0d1-e2f7-0a8b-5c4d-3e2f1a0b9c8d"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "artstation", "ArtStation", null },
                    { new Guid("a7b3c4d5-e6f1-4a2b-9c8d-7e6f5a4b3c2d"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "twitter", "Twitter / X", null },
                    { new Guid("b4c0d1e2-f3a8-1b9c-6d5e-4f3a2b1c0d9e"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "globe", "Personal Website", null },
                    { new Guid("b8c4d5e6-f7a2-5b3c-0d9e-8f7a6b5c4d3e"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "github", "GitHub", null },
                    { new Guid("c9d5e6f7-a8b3-6c4d-1e0f-9a8b7c6d5e4f"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "linkedin", "LinkedIn", null },
                    { new Guid("d0e6f7a8-b9c4-7d5e-2f1a-0b9c8d7e6f5a"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "youtube", "YouTube", null },
                    { new Guid("e1f7a8b9-c0d5-8e6f-3a2b-1c0d9e8f7a6b"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "discord", "Discord", null },
                    { new Guid("f2a8b9c0-d1e6-9f7a-4b3c-2d1e0f9a8b7c"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "instagram", "Instagram", null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_social_platforms_Name",
                table: "social_platforms",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_social_links_PlatformId",
                table: "user_social_links",
                column: "PlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_user_social_links_UserId",
                table: "user_social_links",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "user_social_links");

            migrationBuilder.DropTable(
                name: "social_platforms");

            migrationBuilder.DropColumn(
                name: "AvatarUrl",
                table: "users");

            migrationBuilder.DropColumn(
                name: "Bio",
                table: "users");

            migrationBuilder.DropColumn(
                name: "IsPublicProfile",
                table: "users");
        }
    }
}
