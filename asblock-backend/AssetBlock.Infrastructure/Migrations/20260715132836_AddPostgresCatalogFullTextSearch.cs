using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPostgresCatalogFullTextSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assets_AuthorId",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "IX_assets_CategoryId",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "IX_asset_tags_TagId",
                table: "asset_tags");

            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.AddColumn<NpgsqlTsVector>(
                name: "search_vector",
                table: "assets",
                type: "tsvector",
                nullable: true,
                computedColumnSql: "to_tsvector('simple'::regconfig, coalesce(\"Title\", '') || ' ' || coalesce(\"Description\", ''))",
                stored: true);

            migrationBuilder.CreateIndex(
                name: "IX_assets_catalog_AuthorId_CreatedAt_Id",
                table: "assets",
                columns: new[] { "AuthorId", "CreatedAt", "Id" },
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_assets_catalog_CategoryId_CreatedAt_Id",
                table: "assets",
                columns: new[] { "CategoryId", "CreatedAt", "Id" },
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_assets_catalog_CreatedAt_Id",
                table: "assets",
                columns: new[] { "CreatedAt", "Id" },
                filter: "\"DeletedAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_assets_Description_trgm",
                table: "assets",
                column: "Description")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_assets_search_vector",
                table: "assets",
                column: "search_vector")
                .Annotation("Npgsql:IndexMethod", "GIN");

            migrationBuilder.CreateIndex(
                name: "IX_assets_Title_trgm",
                table: "assets",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_tags_TagId_AssetId",
                table: "asset_tags",
                columns: new[] { "TagId", "AssetId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_assets_catalog_AuthorId_CreatedAt_Id",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "IX_assets_catalog_CategoryId_CreatedAt_Id",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "IX_assets_catalog_CreatedAt_Id",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "IX_assets_Description_trgm",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "IX_assets_search_vector",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "IX_assets_Title_trgm",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "IX_asset_tags_TagId_AssetId",
                table: "asset_tags");

            migrationBuilder.DropColumn(
                name: "search_vector",
                table: "assets");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:pg_trgm", ",,");

            migrationBuilder.CreateIndex(
                name: "IX_assets_AuthorId",
                table: "assets",
                column: "AuthorId");

            migrationBuilder.CreateIndex(
                name: "IX_assets_CategoryId",
                table: "assets",
                column: "CategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_asset_tags_TagId",
                table: "asset_tags",
                column: "TagId");
        }
    }
}
