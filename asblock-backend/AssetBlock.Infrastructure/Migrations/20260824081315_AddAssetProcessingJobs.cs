using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetProcessingJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "asset_processing_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefinitionVersion = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Stage = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LeaseOwner = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    Result = table.Column<string>(type: "jsonb", nullable: true),
                    TraceParent = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_processing_jobs", x => x.Id);
                    table.CheckConstraint("CK_asset_processing_jobs_attempt_count", "\"AttemptCount\" >= 0 AND \"AttemptCount\" <= \"MaxAttempts\"");
                    table.CheckConstraint("CK_asset_processing_jobs_definition_version", "\"DefinitionVersion\" > 0");
                    table.CheckConstraint("CK_asset_processing_jobs_error_code", "\"ErrorCode\" IS NULL OR \"ErrorCode\" ~ '^[A-Z0-9_]{1,64}$'");
                    table.CheckConstraint("CK_asset_processing_jobs_max_attempts", "\"MaxAttempts\" > 0 AND \"MaxAttempts\" <= 10");
                    table.CheckConstraint("CK_asset_processing_jobs_payload_size", "octet_length(CAST(\"Payload\" AS text)) <= 4000");
                    table.CheckConstraint("CK_asset_processing_jobs_payload_type", "jsonb_typeof(\"Payload\") = 'object'");
                    table.CheckConstraint("CK_asset_processing_jobs_result_size", "\"Result\" IS NULL OR octet_length(CAST(\"Result\" AS text)) <= 4000");
                    table.CheckConstraint("CK_asset_processing_jobs_result_type", "\"Result\" IS NULL OR jsonb_typeof(\"Result\") = 'object'");
                    table.CheckConstraint("CK_asset_processing_jobs_running_lease", "(\"Status\" = 'RUNNING' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR (\"Status\" != 'RUNNING' AND \"LeaseOwner\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");
                    table.CheckConstraint("CK_asset_processing_jobs_status", "\"Status\" IN ('QUEUED', 'RUNNING', 'RETRY_SCHEDULED', 'SUCCEEDED', 'FAILED', 'CANCELLED')");
                    table.CheckConstraint("CK_asset_processing_jobs_terminal_completed_at", "(\"Status\" IN ('SUCCEEDED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" NOT IN ('SUCCEEDED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NULL)");
                    table.CheckConstraint("CK_asset_processing_jobs_type", "\"Type\" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN', 'LISTING_COPILOT')");
                    table.ForeignKey(
                        name: "FK_asset_processing_jobs_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_asset_processing_jobs_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_asset_processing_jobs_AssetId_AssetVersionId",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_processing_jobs_claim",
                table: "asset_processing_jobs",
                columns: new[] { "Status", "AvailableAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_processing_jobs_lease_expiry",
                table: "asset_processing_jobs",
                column: "LeaseExpiresAt");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_processing_jobs_idempotency",
                table: "asset_processing_jobs",
                columns: new[] { "AssetVersionId", "Type", "DefinitionVersion" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_processing_jobs");
        }
    }
}
