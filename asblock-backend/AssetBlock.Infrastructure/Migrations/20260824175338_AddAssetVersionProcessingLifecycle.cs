using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetVersionProcessingLifecycle : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ProcessingErrorCode",
                table: "asset_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcessingErrorSummary",
                table: "asset_versions",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ProcessingStatus",
                table: "asset_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ProcessingUpdatedAt",
                table: "asset_versions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql(
                """
                UPDATE asset_versions
                SET "ProcessingStatus" = 'READY',
                    "ProcessingUpdatedAt" = "CreatedAt",
                    "ProcessingErrorCode" = NULL,
                    "ProcessingErrorSummary" = NULL;
                """);

            migrationBuilder.AlterColumn<string>(
                name: "ProcessingStatus",
                table: "asset_versions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<DateTimeOffset>(
                name: "ProcessingUpdatedAt",
                table: "asset_versions",
                type: "timestamp with time zone",
                nullable: false,
                oldClrType: typeof(DateTimeOffset),
                oldType: "timestamp with time zone",
                oldNullable: true);

            migrationBuilder.Sql(
                """
                ALTER TABLE asset_versions ALTER COLUMN "ProcessingStatus" SET NOT NULL;
                ALTER TABLE asset_versions ALTER COLUMN "ProcessingStatus" DROP DEFAULT;
                ALTER TABLE asset_versions ALTER COLUMN "ProcessingUpdatedAt" SET NOT NULL;
                ALTER TABLE asset_versions ALTER COLUMN "ProcessingUpdatedAt" DROP DEFAULT;
                """);

            migrationBuilder.CreateTable(
                name: "asset_archive_analyses",
                columns: table => new
                {
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    FileCount = table.Column<int>(type: "integer", nullable: false),
                    TotalExpandedBytes = table.Column<long>(type: "bigint", nullable: false),
                    ReadmeContent = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    ManifestMetadata = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_archive_analyses", x => x.AssetVersionId);
                    table.CheckConstraint("CK_asset_archive_analyses_file_count", "\"FileCount\" >= 0");
                    table.CheckConstraint("CK_asset_archive_analyses_manifest_metadata", "\"ManifestMetadata\" IS NULL OR jsonb_typeof(\"ManifestMetadata\") = 'object'");
                    table.CheckConstraint("CK_asset_archive_analyses_manifest_metadata_size", "\"ManifestMetadata\" IS NULL OR octet_length(CAST(\"ManifestMetadata\" AS text)) <= 16384");
                    table.CheckConstraint("CK_asset_archive_analyses_readme_content_size", "\"ReadmeContent\" IS NULL OR octet_length(\"ReadmeContent\") <= 16384");
                    table.CheckConstraint("CK_asset_archive_analyses_total_expanded_bytes", "\"TotalExpandedBytes\" >= 0");
                    table.ForeignKey(
                        name: "FK_asset_archive_analyses_asset_versions_AssetVersionId",
                        column: x => x.AssetVersionId,
                        principalTable: "asset_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_versions_processing_status",
                table: "asset_versions",
                column: "ProcessingStatus");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_versions_processing_error_code",
                table: "asset_versions",
                sql: "\"ProcessingErrorCode\" IS NULL OR \"ProcessingErrorCode\" ~ '^[A-Z0-9_]{1,64}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_versions_processing_status",
                table: "asset_versions",
                sql: "\"ProcessingStatus\" IN ('PENDING_INSPECTION', 'PENDING_MALWARE_SCAN', 'READY', 'REJECTED', 'PROCESSING_FAILED')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_versions_ready_current",
                table: "asset_versions",
                sql: "\"IsCurrent\" = false OR (\"IsCurrent\" = true AND \"ProcessingStatus\" = 'READY')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_asset_versions_state_error_consistency",
                table: "asset_versions",
                sql: "(\"ProcessingStatus\" IN ('PENDING_INSPECTION', 'PENDING_MALWARE_SCAN', 'READY') AND \"ProcessingErrorCode\" IS NULL AND \"ProcessingErrorSummary\" IS NULL) OR (\"ProcessingStatus\" IN ('REJECTED', 'PROCESSING_FAILED') AND \"ProcessingErrorCode\" IS NOT NULL AND \"ProcessingErrorSummary\" IS NOT NULL AND length(trim(\"ProcessingErrorSummary\")) > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_archive_analyses");

            migrationBuilder.DropIndex(
                name: "IX_asset_versions_processing_status",
                table: "asset_versions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_versions_processing_error_code",
                table: "asset_versions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_versions_processing_status",
                table: "asset_versions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_versions_ready_current",
                table: "asset_versions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_asset_versions_state_error_consistency",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "ProcessingErrorCode",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "ProcessingErrorSummary",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "ProcessingStatus",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "ProcessingUpdatedAt",
                table: "asset_versions");
        }
    }
}
