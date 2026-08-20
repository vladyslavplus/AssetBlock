using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAssetVersionsAndContentProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AssetVersionId",
                table: "purchases",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<Guid>(
                name: "CheckoutIntentId",
                table: "purchases",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "purchases",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<decimal>(
                name: "PricePaid",
                table: "purchases",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "asset_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContentLength = table.Column<long>(type: "bigint", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReleaseNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    LicenseCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LicenseTemplateVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LicenseDisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LicenseTerms = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_versions", x => x.Id);
                    table.CheckConstraint("CK_asset_versions_content_length_positive", "\"ContentLength\" > 0");
                    table.CheckConstraint("CK_asset_versions_version_number_positive", "\"VersionNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_asset_versions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "checkout_intents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    UnitAmount = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    StripeSessionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_intents", x => x.Id);
                    table.CheckConstraint("CK_checkout_intents_expires_after_created", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_checkout_intents_unit_amount_positive", "\"UnitAmount\" > 0");
                    table.ForeignKey(
                        name: "FK_checkout_intents_asset_versions_AssetVersionId",
                        column: x => x.AssetVersionId,
                        principalTable: "asset_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_checkout_intents_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_checkout_intents_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_purchases_AssetVersionId",
                table: "purchases",
                column: "AssetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_purchases_CheckoutIntentId",
                table: "purchases",
                column: "CheckoutIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_asset_versions_asset_current",
                table: "asset_versions",
                column: "AssetId",
                unique: true,
                filter: "\"IsCurrent\" = true");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_versions_asset_number",
                table: "asset_versions",
                columns: new[] { "AssetId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_asset_versions_storage_key",
                table: "asset_versions",
                column: "StorageKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intents_asset_active",
                table: "checkout_intents",
                columns: new[] { "AssetId", "Status", "ExpiresAt" });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intents_AssetVersionId",
                table: "checkout_intents",
                column: "AssetVersionId");

            migrationBuilder.CreateIndex(
                name: "UIX_checkout_intents_stripe_session",
                table: "checkout_intents",
                column: "StripeSessionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_checkout_intents_user_asset_pending",
                table: "checkout_intents",
                columns: new[] { "UserId", "AssetId" },
                unique: true,
                filter: "\"Status\" = 'PENDING'");

            migrationBuilder.AddForeignKey(
                name: "FK_purchases_asset_versions_AssetVersionId",
                table: "purchases",
                column: "AssetVersionId",
                principalTable: "asset_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_purchases_checkout_intents_CheckoutIntentId",
                table: "purchases",
                column: "CheckoutIntentId",
                principalTable: "checkout_intents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_purchases_asset_versions_AssetVersionId",
                table: "purchases");

            migrationBuilder.DropForeignKey(
                name: "FK_purchases_checkout_intents_CheckoutIntentId",
                table: "purchases");

            migrationBuilder.DropTable(
                name: "checkout_intents");

            migrationBuilder.DropTable(
                name: "asset_versions");

            migrationBuilder.DropIndex(
                name: "IX_purchases_AssetVersionId",
                table: "purchases");

            migrationBuilder.DropIndex(
                name: "IX_purchases_CheckoutIntentId",
                table: "purchases");

            migrationBuilder.DropColumn(
                name: "AssetVersionId",
                table: "purchases");

            migrationBuilder.DropColumn(
                name: "CheckoutIntentId",
                table: "purchases");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "purchases");

            migrationBuilder.DropColumn(
                name: "PricePaid",
                table: "purchases");
        }
    }
}
