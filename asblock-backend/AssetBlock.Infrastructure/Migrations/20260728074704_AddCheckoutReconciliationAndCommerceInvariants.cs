using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCheckoutReconciliationAndCommerceInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_checkout_intent_items_asset_versions_AssetVersionId",
                table: "checkout_intent_items");

            migrationBuilder.DropForeignKey(
                name: "FK_order_lines_asset_versions_AssetVersionId",
                table: "order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_purchases_asset_versions_AssetVersionId",
                table: "purchases");

            migrationBuilder.DropIndex(
                name: "IX_purchases_AssetId",
                table: "purchases");

            migrationBuilder.DropIndex(
                name: "IX_purchases_AssetVersionId",
                table: "purchases");

            migrationBuilder.DropIndex(
                name: "IX_order_lines_AssetId",
                table: "order_lines");

            migrationBuilder.DropIndex(
                name: "IX_order_lines_AssetVersionId",
                table: "order_lines");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intent_items_AssetVersionId",
                table: "checkout_intent_items");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LastStripeReconciledAt",
                table: "checkout_intents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_asset_versions_AssetId_Id",
                table: "asset_versions",
                columns: new[] { "AssetId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_purchases_AssetId_AssetVersionId",
                table: "purchases",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_orders_currency_iso_lower",
                table: "orders",
                sql: "\"Currency\" ~ '^[a-z]{3}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_orders_currency_usd_v1",
                table: "orders",
                sql: "\"Currency\" = 'usd'");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_AssetId_AssetVersionId",
                table: "order_lines",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intents_pending_attached_reconcile",
                table: "checkout_intents",
                columns: new[] { "Status", "CreatedAt", "Id" },
                filter: "\"Status\" = 'PENDING' AND \"StripeSessionId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_checkout_intents_currency_iso_lower",
                table: "checkout_intents",
                sql: "\"Currency\" ~ '^[a-z]{3}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_checkout_intents_currency_usd_v1",
                table: "checkout_intents",
                sql: "\"Currency\" = 'usd'");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_AssetId_AssetVersionId",
                table: "checkout_intent_items",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_bundle_revisions_currency_iso_lower",
                table: "bundle_revisions",
                sql: "\"Currency\" ~ '^[a-z]{3}$'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_bundle_revisions_currency_usd_v1",
                table: "bundle_revisions",
                sql: "\"Currency\" = 'usd'");

            migrationBuilder.AddForeignKey(
                name: "FK_checkout_intent_items_asset_versions_AssetId_AssetVersionId",
                table: "checkout_intent_items",
                columns: new[] { "AssetId", "AssetVersionId" },
                principalTable: "asset_versions",
                principalColumns: new[] { "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_order_lines_asset_versions_AssetId_AssetVersionId",
                table: "order_lines",
                columns: new[] { "AssetId", "AssetVersionId" },
                principalTable: "asset_versions",
                principalColumns: new[] { "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_purchases_asset_versions_AssetId_AssetVersionId",
                table: "purchases",
                columns: new[] { "AssetId", "AssetVersionId" },
                principalTable: "asset_versions",
                principalColumns: new[] { "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_checkout_intent_items_asset_versions_AssetId_AssetVersionId",
                table: "checkout_intent_items");

            migrationBuilder.DropForeignKey(
                name: "FK_order_lines_asset_versions_AssetId_AssetVersionId",
                table: "order_lines");

            migrationBuilder.DropForeignKey(
                name: "FK_purchases_asset_versions_AssetId_AssetVersionId",
                table: "purchases");

            migrationBuilder.DropIndex(
                name: "IX_purchases_AssetId_AssetVersionId",
                table: "purchases");

            migrationBuilder.DropCheckConstraint(
                name: "CK_orders_currency_iso_lower",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "CK_orders_currency_usd_v1",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_order_lines_AssetId_AssetVersionId",
                table: "order_lines");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intents_pending_attached_reconcile",
                table: "checkout_intents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_checkout_intents_currency_iso_lower",
                table: "checkout_intents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_checkout_intents_currency_usd_v1",
                table: "checkout_intents");

            migrationBuilder.DropIndex(
                name: "IX_checkout_intent_items_AssetId_AssetVersionId",
                table: "checkout_intent_items");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bundle_revisions_currency_iso_lower",
                table: "bundle_revisions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_bundle_revisions_currency_usd_v1",
                table: "bundle_revisions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_asset_versions_AssetId_Id",
                table: "asset_versions");

            migrationBuilder.DropColumn(
                name: "LastStripeReconciledAt",
                table: "checkout_intents");

            migrationBuilder.CreateIndex(
                name: "IX_purchases_AssetId",
                table: "purchases",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_purchases_AssetVersionId",
                table: "purchases",
                column: "AssetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_AssetId",
                table: "order_lines",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_AssetVersionId",
                table: "order_lines",
                column: "AssetVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_AssetVersionId",
                table: "checkout_intent_items",
                column: "AssetVersionId");

            migrationBuilder.AddForeignKey(
                name: "FK_checkout_intent_items_asset_versions_AssetVersionId",
                table: "checkout_intent_items",
                column: "AssetVersionId",
                principalTable: "asset_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_order_lines_asset_versions_AssetVersionId",
                table: "order_lines",
                column: "AssetVersionId",
                principalTable: "asset_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_purchases_asset_versions_AssetVersionId",
                table: "purchases",
                column: "AssetVersionId",
                principalTable: "asset_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }
    }
}
