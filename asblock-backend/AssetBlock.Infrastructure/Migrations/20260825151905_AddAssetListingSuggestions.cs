using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetListingSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "asset_listing_suggestions",
                columns: table => new
                {
                    JobId = table.Column<Guid>(type: "uuid", nullable: false),
                    PromptPolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ModelRevision = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    UpstreamProvider = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ProviderRequestId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: false),
                    Category = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Tags = table.Column<string>(type: "jsonb", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    InputTokens = table.Column<int>(type: "integer", nullable: true),
                    OutputTokens = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_listing_suggestions", x => x.JobId);
                    table.CheckConstraint("CK_asset_listing_suggestions_content_hash", "\"ContentHash\" ~ '^[a-f0-9]{64}$'");
                    table.CheckConstraint("CK_asset_listing_suggestions_input_tokens", "\"InputTokens\" IS NULL OR \"InputTokens\" >= 0");
                    table.CheckConstraint("CK_asset_listing_suggestions_output_tokens", "\"OutputTokens\" IS NULL OR \"OutputTokens\" >= 0");
                    table.CheckConstraint("CK_asset_listing_suggestions_provider", "\"Provider\" IN ('OPENROUTER', 'OLLAMA')");
                    table.CheckConstraint("CK_asset_listing_suggestions_tags_items", "NOT jsonb_path_exists(\"Tags\", '$[*] ? (@.type() != \"string\")')");
                    table.CheckConstraint("CK_asset_listing_suggestions_tags_length", "jsonb_array_length(\"Tags\") <= 10");
                    table.CheckConstraint("CK_asset_listing_suggestions_tags_size", "octet_length(CAST(\"Tags\" AS text)) <= 4000");
                    table.CheckConstraint("CK_asset_listing_suggestions_tags_type", "jsonb_typeof(\"Tags\") = 'array'");
                    table.ForeignKey(
                        name: "FK_asset_listing_suggestions_asset_processing_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "asset_processing_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "asset_listing_suggestions");
        }
    }
}
