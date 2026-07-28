using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSellerAnalyticsQueryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_reviews_asset_created_id",
                table: "reviews",
                columns: new[] { "AssetId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_orders_bundle_purchased_id",
                table: "orders",
                columns: new[] { "BundleId", "PurchasedAt", "Id" },
                filter: "\"BundleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_orders_purchased_id",
                table: "orders",
                columns: new[] { "PurchasedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_seller_asset_order",
                table: "order_lines",
                columns: new[] { "SellerId", "AssetId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_seller_intent",
                table: "checkout_intent_items",
                columns: new[] { "SellerId", "CheckoutIntentId" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_author_id",
                table: "assets",
                columns: new[] { "AuthorId", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_reviews_asset_created_id",
                table: "reviews");

            migrationBuilder.DropIndex(
                name: "IX_orders_bundle_purchased_id",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_purchased_id",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_order_lines_seller_asset_order",
                table: "order_lines");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intent_items_seller_intent",
                table: "checkout_intent_items");

            migrationBuilder.DropIndex(
                name: "IX_assets_author_id",
                table: "assets");
        }
    }
}
