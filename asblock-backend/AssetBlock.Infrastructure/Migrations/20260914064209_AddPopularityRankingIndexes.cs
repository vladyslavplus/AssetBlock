using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPopularityRankingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_recommendation_daily_source_target_day",
                table: "recommendation_daily",
                columns: new[] { "SourceAssetId", "TargetAssetId", "DayUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_product_analytics_daily_type_product_day",
                table: "product_analytics_daily",
                columns: new[] { "ProductType", "ProductId", "DayUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_recommendation_daily_source_target_day",
                table: "recommendation_daily");

            migrationBuilder.DropIndex(
                name: "IX_product_analytics_daily_type_product_day",
                table: "product_analytics_daily");
        }
    }
}
