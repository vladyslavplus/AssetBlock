using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingJobState : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "UIX_asset_processing_jobs_idempotency",
                table: "asset_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_processing_jobs_type",
                table: "asset_processing_jobs");

            migrationBuilder.AddColumn<long>(
                name: "SearchRevision",
                table: "assets",
                type: "bigint",
                nullable: false,
                defaultValue: 1L);

            migrationBuilder.AddColumn<string>(
                name: "InputHash",
                table: "asset_processing_jobs",
                type: "char(64)",
                fixedLength: true,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelKey",
                table: "asset_processing_jobs",
                type: "char(64)",
                fixedLength: true,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_assets_search_revision",
                table: "assets",
                sql: "\"SearchRevision\" > 0");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_processing_jobs_embedding_active",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "Type", "DefinitionVersion", "ModelKey", "InputHash" },
                unique: true,
                filter: "\"Type\" = 'EMBEDDING_GENERATION' AND \"Status\" IN ('QUEUED', 'RUNNING', 'RETRY_SCHEDULED')");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_processing_jobs_idempotency",
                table: "asset_processing_jobs",
                columns: new[] { "AssetVersionId", "Type", "DefinitionVersion" },
                unique: true,
                filter: "\"Type\" <> 'EMBEDDING_GENERATION'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_processing_jobs_embedding_hashes",
                table: "asset_processing_jobs",
                sql: "(\"Type\" = 'EMBEDDING_GENERATION' AND \"InputHash\" IS NOT NULL AND \"InputHash\" ~ '^[0-9a-f]{64}$' AND \"ModelKey\" IS NOT NULL AND \"ModelKey\" ~ '^[0-9a-f]{64}$') OR (\"Type\" <> 'EMBEDDING_GENERATION' AND \"InputHash\" IS NULL AND \"ModelKey\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_processing_jobs_type",
                table: "asset_processing_jobs",
                sql: "\"Type\" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN', 'LISTING_COPILOT', 'EMBEDDING_GENERATION')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_assets_search_revision",
                table: "assets");

            migrationBuilder.DropIndex(
                name: "UIX_asset_processing_jobs_embedding_active",
                table: "asset_processing_jobs");

            migrationBuilder.DropIndex(
                name: "UIX_asset_processing_jobs_idempotency",
                table: "asset_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_processing_jobs_embedding_hashes",
                table: "asset_processing_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_processing_jobs_type",
                table: "asset_processing_jobs");

            migrationBuilder.DropColumn(
                name: "SearchRevision",
                table: "assets");

            migrationBuilder.DropColumn(
                name: "InputHash",
                table: "asset_processing_jobs");

            migrationBuilder.DropColumn(
                name: "ModelKey",
                table: "asset_processing_jobs");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_processing_jobs_idempotency",
                table: "asset_processing_jobs",
                columns: new[] { "AssetVersionId", "Type", "DefinitionVersion" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_processing_jobs_type",
                table: "asset_processing_jobs",
                sql: "\"Type\" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN', 'LISTING_COPILOT')");
        }
    }
}
