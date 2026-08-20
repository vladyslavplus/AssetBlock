using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCollectionsBundlesAndOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_checkout_intents_asset_versions_AssetVersionId",
                table: "checkout_intents");

            migrationBuilder.DropForeignKey(
                name: "FK_purchases_checkout_intents_CheckoutIntentId",
                table: "purchases");

            migrationBuilder.DropIndex(
                name: "IX_purchases_StripePaymentId",
                table: "purchases");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intents_asset_active",
                table: "checkout_intents");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intents_AssetVersionId",
                table: "checkout_intents");

            migrationBuilder.DropIndex(
                name: "UIX_checkout_intents_user_asset_pending",
                table: "checkout_intents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_checkout_intents_unit_amount_positive",
                table: "checkout_intents");

            migrationBuilder.DropColumn(
                name: "Currency",
                table: "purchases");

            migrationBuilder.DropColumn(
                name: "PricePaid",
                table: "purchases");

            migrationBuilder.DropColumn(
                name: "StripePaymentId",
                table: "purchases");

            migrationBuilder.DropColumn(
                name: "AssetVersionId",
                table: "checkout_intents");

            migrationBuilder.RenameColumn(
                name: "CheckoutIntentId",
                table: "purchases",
                newName: "OrderLineId");

            migrationBuilder.RenameIndex(
                name: "IX_purchases_UserId_AssetId",
                table: "purchases",
                newName: "UIX_purchases_user_asset");

            migrationBuilder.RenameIndex(
                name: "IX_purchases_CheckoutIntentId",
                table: "purchases",
                newName: "UIX_purchases_order_line");

            migrationBuilder.RenameColumn(
                name: "UnitAmount",
                table: "checkout_intents",
                newName: "AmountTotal");

            migrationBuilder.RenameColumn(
                name: "AssetTitle",
                table: "checkout_intents",
                newName: "ProductTitle");

            migrationBuilder.AlterColumn<Guid>(
                name: "AssetId",
                table: "checkout_intents",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "BundleId",
                table: "checkout_intents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "BundleRevisionId",
                table: "checkout_intents",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "bundles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bundles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_bundles_users_SellerId",
                        column: x => x.SellerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "checkout_intent_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckoutIntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    AssetTitleSnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    ListPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    AllocatedPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LicenseCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LicenseTemplateVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LicenseDisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LicenseTerms = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_intent_items", x => x.Id);
                    table.CheckConstraint("CK_checkout_intent_items_allocated_price_positive", "\"AllocatedPrice\" > 0");
                    table.CheckConstraint("CK_checkout_intent_items_list_price_positive", "\"ListPrice\" > 0");
                    table.CheckConstraint("CK_checkout_intent_items_position_positive", "\"Position\" > 0");
                    table.CheckConstraint("CK_checkout_intent_items_version_positive", "\"VersionNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_checkout_intent_items_asset_versions_AssetVersionId",
                        column: x => x.AssetVersionId,
                        principalTable: "asset_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_checkout_intent_items_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_checkout_intent_items_checkout_intents_CheckoutIntentId",
                        column: x => x.CheckoutIntentId,
                        principalTable: "checkout_intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_checkout_intent_items_users_SellerId",
                        column: x => x.SellerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "checkout_reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckoutIntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_reservations", x => x.Id);
                    table.CheckConstraint("CK_checkout_reservations_expires_after_created", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_checkout_reservations_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_checkout_reservations_checkout_intents_CheckoutIntentId",
                        column: x => x.CheckoutIntentId,
                        principalTable: "checkout_intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_checkout_reservations_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "collections",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_collections_users_SellerId",
                        column: x => x.SellerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "bundle_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BundleId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNumber = table.Column<int>(type: "integer", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    Title = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Description = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    ListPriceTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bundle_revisions", x => x.Id);
                    table.CheckConstraint("CK_bundle_revisions_list_price_total_positive", "\"ListPriceTotal\" > 0");
                    table.CheckConstraint("CK_bundle_revisions_price_below_list_total", "\"Price\" < \"ListPriceTotal\"");
                    table.CheckConstraint("CK_bundle_revisions_price_positive", "\"Price\" > 0");
                    table.CheckConstraint("CK_bundle_revisions_revision_number_positive", "\"RevisionNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_bundle_revisions_bundles_BundleId",
                        column: x => x.BundleId,
                        principalTable: "bundles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "collection_items",
                columns: table => new
                {
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_items", x => new { x.CollectionId, x.AssetId });
                    table.CheckConstraint("CK_collection_items_position_positive", "\"Position\" > 0");
                    table.ForeignKey(
                        name: "FK_collection_items_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_collection_items_collections_CollectionId",
                        column: x => x.CollectionId,
                        principalTable: "collections",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "bundle_revision_items",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BundleRevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    AssetTitleSnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    ListPriceSnapshot = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_bundle_revision_items", x => x.Id);
                    table.CheckConstraint("CK_bundle_revision_items_list_price_positive", "\"ListPriceSnapshot\" > 0");
                    table.CheckConstraint("CK_bundle_revision_items_position_positive", "\"Position\" > 0");
                    table.ForeignKey(
                        name: "FK_bundle_revision_items_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_bundle_revision_items_bundle_revisions_BundleRevisionId",
                        column: x => x.BundleRevisionId,
                        principalTable: "bundle_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "orders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckoutIntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    BundleId = table.Column<Guid>(type: "uuid", nullable: true),
                    BundleRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    StripeSessionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    AmountPaid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    PurchasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_orders", x => x.Id);
                    table.CheckConstraint("CK_orders_amount_paid_positive", "\"AmountPaid\" > 0");
                    table.CheckConstraint("CK_orders_exactly_one_product", "(\"AssetId\" IS NOT NULL AND \"BundleId\" IS NULL AND \"BundleRevisionId\" IS NULL)\r\nOR (\"AssetId\" IS NULL AND \"BundleId\" IS NOT NULL AND \"BundleRevisionId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_orders_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_bundle_revisions_BundleRevisionId",
                        column: x => x.BundleRevisionId,
                        principalTable: "bundle_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_bundles_BundleId",
                        column: x => x.BundleId,
                        principalTable: "bundles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_checkout_intents_CheckoutIntentId",
                        column: x => x.CheckoutIntentId,
                        principalTable: "checkout_intents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_orders_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "order_lines",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    AssetTitleSnapshot = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    ListPrice = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    PricePaid = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    LicenseCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LicenseTemplateVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LicenseDisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LicenseTerms = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_lines", x => x.Id);
                    table.CheckConstraint("CK_order_lines_list_price_positive", "\"ListPrice\" > 0");
                    table.CheckConstraint("CK_order_lines_position_positive", "\"Position\" > 0");
                    table.CheckConstraint("CK_order_lines_price_paid_positive", "\"PricePaid\" > 0");
                    table.CheckConstraint("CK_order_lines_version_positive", "\"VersionNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_order_lines_asset_versions_AssetVersionId",
                        column: x => x.AssetVersionId,
                        principalTable: "asset_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_lines_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_lines_orders_OrderId",
                        column: x => x.OrderId,
                        principalTable: "orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_lines_users_SellerId",
                        column: x => x.SellerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intents_AssetId",
                table: "checkout_intents",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intents_BundleId",
                table: "checkout_intents",
                column: "BundleId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intents_BundleRevisionId",
                table: "checkout_intents",
                column: "BundleRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intents_status_expires",
                table: "checkout_intents",
                columns: new[] { "Status", "ExpiresAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "UIX_checkout_intents_user_asset_pending",
                table: "checkout_intents",
                columns: new[] { "UserId", "AssetId" },
                unique: true,
                filter: "\"Status\" = 'PENDING' AND \"AssetId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_checkout_intents_user_bundle_pending",
                table: "checkout_intents",
                columns: new[] { "UserId", "BundleId" },
                unique: true,
                filter: "\"Status\" = 'PENDING' AND \"BundleId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_checkout_intents_amount_total_positive",
                table: "checkout_intents",
                sql: "\"AmountTotal\" > 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_checkout_intents_exactly_one_product",
                table: "checkout_intents",
                sql: "(\"AssetId\" IS NOT NULL AND \"BundleId\" IS NULL AND \"BundleRevisionId\" IS NULL)\nOR (\"AssetId\" IS NULL AND \"BundleId\" IS NOT NULL AND \"BundleRevisionId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_bundle_revision_items_AssetId",
                table: "bundle_revision_items",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "UIX_bundle_revision_items_revision_asset",
                table: "bundle_revision_items",
                columns: new[] { "BundleRevisionId", "AssetId" },
                unique: true,
                filter: "\"AssetId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_bundle_revision_items_revision_position",
                table: "bundle_revision_items",
                columns: new[] { "BundleRevisionId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_bundle_revisions_bundle_current",
                table: "bundle_revisions",
                column: "BundleId",
                unique: true,
                filter: "\"IsCurrent\" = true");

            migrationBuilder.CreateIndex(
                name: "UIX_bundle_revisions_bundle_number",
                table: "bundle_revisions",
                columns: new[] { "BundleId", "RevisionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bundles_archived_created",
                table: "bundles",
                columns: new[] { "ArchivedAt", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_bundles_seller_created",
                table: "bundles",
                columns: new[] { "SellerId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_asset",
                table: "checkout_intent_items",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_AssetVersionId",
                table: "checkout_intent_items",
                column: "AssetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_SellerId",
                table: "checkout_intent_items",
                column: "SellerId");

            migrationBuilder.CreateIndex(
                name: "UIX_checkout_intent_items_intent_asset",
                table: "checkout_intent_items",
                columns: new[] { "CheckoutIntentId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_checkout_intent_items_intent_position",
                table: "checkout_intent_items",
                columns: new[] { "CheckoutIntentId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_checkout_reservations_AssetId",
                table: "checkout_reservations",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_reservations_expires",
                table: "checkout_reservations",
                columns: new[] { "ExpiresAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "UIX_checkout_reservations_intent_asset",
                table: "checkout_reservations",
                columns: new[] { "CheckoutIntentId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_checkout_reservations_user_asset",
                table: "checkout_reservations",
                columns: new[] { "UserId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_collection_items_AssetId",
                table: "collection_items",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "UIX_collection_items_collection_position",
                table: "collection_items",
                columns: new[] { "CollectionId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_collections_public_status_published",
                table: "collections",
                columns: new[] { "Status", "PublishedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_collections_seller_status_created",
                table: "collections",
                columns: new[] { "SellerId", "Status", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_AssetId",
                table: "order_lines",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_AssetVersionId",
                table: "order_lines",
                column: "AssetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_seller_order",
                table: "order_lines",
                columns: new[] { "SellerId", "OrderId" });

            migrationBuilder.CreateIndex(
                name: "UIX_order_lines_order_asset",
                table: "order_lines",
                columns: new[] { "OrderId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_order_lines_order_position",
                table: "order_lines",
                columns: new[] { "OrderId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_AssetId",
                table: "orders",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_BundleId",
                table: "orders",
                column: "BundleId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_BundleRevisionId",
                table: "orders",
                column: "BundleRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_user_purchased",
                table: "orders",
                columns: new[] { "UserId", "PurchasedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "UIX_orders_checkout_intent",
                table: "orders",
                column: "CheckoutIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_orders_stripe_session",
                table: "orders",
                column: "StripeSessionId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_checkout_intents_bundle_revisions_BundleRevisionId",
                table: "checkout_intents",
                column: "BundleRevisionId",
                principalTable: "bundle_revisions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_checkout_intents_bundles_BundleId",
                table: "checkout_intents",
                column: "BundleId",
                principalTable: "bundles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_purchases_order_lines_OrderLineId",
                table: "purchases",
                column: "OrderLineId",
                principalTable: "order_lines",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_checkout_intents_bundle_revisions_BundleRevisionId",
                table: "checkout_intents");

            migrationBuilder.DropForeignKey(
                name: "FK_checkout_intents_bundles_BundleId",
                table: "checkout_intents");

            migrationBuilder.DropForeignKey(
                name: "FK_purchases_order_lines_OrderLineId",
                table: "purchases");

            migrationBuilder.DropTable(
                name: "bundle_revision_items");

            migrationBuilder.DropTable(
                name: "checkout_intent_items");

            migrationBuilder.DropTable(
                name: "checkout_reservations");

            migrationBuilder.DropTable(
                name: "collection_items");

            migrationBuilder.DropTable(
                name: "order_lines");

            migrationBuilder.DropTable(
                name: "collections");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "bundle_revisions");

            migrationBuilder.DropTable(
                name: "bundles");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intents_AssetId",
                table: "checkout_intents");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intents_BundleId",
                table: "checkout_intents");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intents_BundleRevisionId",
                table: "checkout_intents");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intents_status_expires",
                table: "checkout_intents");

            migrationBuilder.DropIndex(
                name: "UIX_checkout_intents_user_asset_pending",
                table: "checkout_intents");

            migrationBuilder.DropIndex(
                name: "UIX_checkout_intents_user_bundle_pending",
                table: "checkout_intents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_checkout_intents_amount_total_positive",
                table: "checkout_intents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_checkout_intents_exactly_one_product",
                table: "checkout_intents");

            migrationBuilder.DropColumn(
                name: "BundleId",
                table: "checkout_intents");

            migrationBuilder.DropColumn(
                name: "BundleRevisionId",
                table: "checkout_intents");

            migrationBuilder.RenameColumn(
                name: "OrderLineId",
                table: "purchases",
                newName: "CheckoutIntentId");

            migrationBuilder.RenameIndex(
                name: "UIX_purchases_user_asset",
                table: "purchases",
                newName: "IX_purchases_UserId_AssetId");

            migrationBuilder.RenameIndex(
                name: "UIX_purchases_order_line",
                table: "purchases",
                newName: "IX_purchases_CheckoutIntentId");

            migrationBuilder.RenameColumn(
                name: "ProductTitle",
                table: "checkout_intents",
                newName: "AssetTitle");

            migrationBuilder.RenameColumn(
                name: "AmountTotal",
                table: "checkout_intents",
                newName: "UnitAmount");

            migrationBuilder.AddColumn<string>(
                name: "Currency",
                table: "purchases",
                type: "character varying(3)",
                maxLength: 3,
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

            migrationBuilder.AddColumn<string>(
                name: "StripePaymentId",
                table: "purchases",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AlterColumn<Guid>(
                name: "AssetId",
                table: "checkout_intents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AssetVersionId",
                table: "checkout_intents",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"));

            migrationBuilder.CreateIndex(
                name: "IX_purchases_StripePaymentId",
                table: "purchases",
                column: "StripePaymentId",
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
                name: "UIX_checkout_intents_user_asset_pending",
                table: "checkout_intents",
                columns: new[] { "UserId", "AssetId" },
                unique: true,
                filter: "\"Status\" = 'PENDING'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_checkout_intents_unit_amount_positive",
                table: "checkout_intents",
                sql: "\"UnitAmount\" > 0");

            migrationBuilder.AddForeignKey(
                name: "FK_checkout_intents_asset_versions_AssetVersionId",
                table: "checkout_intents",
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
    }
}
