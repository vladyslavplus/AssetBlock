using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class OptimizePersistenceQueriesBatch7 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_recipient_unread_created_id",
                table: "user_notifications",
                columns: new[] { "RecipientUserId", "CreatedAt", "Id" },
                descending: new[] { false, true, false },
                filter: "\"ReadAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_purchases_user_purchased_at_id",
                table: "purchases",
                columns: new[] { "UserId", "PurchasedAt", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "IX_collections_Description_trgm",
                table: "collections",
                column: "Description")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_collections_Title_trgm",
                table: "collections",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_categories_Description_trgm",
                table: "categories",
                column: "Description")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_categories_Name_trgm",
                table: "categories",
                column: "Name")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_categories_Slug_trgm",
                table: "categories",
                column: "Slug")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_bundle_revisions_Description_trgm",
                table: "bundle_revisions",
                column: "Description")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_bundle_revisions_Title_trgm",
                table: "bundle_revisions",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_user_notifications_recipient_unread_created_id",
                table: "user_notifications");

            migrationBuilder.DropIndex(
                name: "IX_purchases_user_purchased_at_id",
                table: "purchases");

            migrationBuilder.DropIndex(
                name: "IX_collections_Description_trgm",
                table: "collections");

            migrationBuilder.DropIndex(
                name: "IX_collections_Title_trgm",
                table: "collections");

            migrationBuilder.DropIndex(
                name: "IX_categories_Description_trgm",
                table: "categories");

            migrationBuilder.DropIndex(
                name: "IX_categories_Name_trgm",
                table: "categories");

            migrationBuilder.DropIndex(
                name: "IX_categories_Slug_trgm",
                table: "categories");

            migrationBuilder.DropIndex(
                name: "IX_bundle_revisions_Description_trgm",
                table: "bundle_revisions");

            migrationBuilder.DropIndex(
                name: "IX_bundle_revisions_Title_trgm",
                table: "bundle_revisions");
        }
    }
}
