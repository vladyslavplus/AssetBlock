using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizeDataAccessLifecycleBatch8 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "RatingAverage",
                table: "assets",
                type: "double precision",
                nullable: false,
                defaultValue: 0.0);

            migrationBuilder.AddColumn<int>(
                name: "RatingCount",
                table: "assets",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_expires_at",
                table: "refresh_tokens",
                column: "ExpiresAt");

            migrationBuilder.Sql("""
                UPDATE assets a
                SET "RatingCount" = agg.count,
                    "RatingAverage" = agg.avg
                FROM (
                    SELECT "AssetId", COUNT(*)::int AS count, AVG("Rating")::double precision AS avg
                    FROM reviews
                    GROUP BY "AssetId"
                ) agg
                WHERE a."Id" = agg."AssetId";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_refresh_tokens_expires_at",
                table: "refresh_tokens");

            migrationBuilder.DropColumn(
                name: "RatingAverage",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "RatingCount",
                table: "assets");
        }
    }
}
