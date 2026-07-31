using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSellerEngagementAnalytics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "AnalyticsSessionId",
                table: "checkout_intents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AnalyticsVisitorId",
                table: "checkout_intents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AttributionCollectionId",
                table: "checkout_intents",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionReferrerHost",
                table: "checkout_intents",
                type: "character varying(253)",
                maxLength: 253,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AttributionSource",
                table: "checkout_intents",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "analytics_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    VisitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    BundleId = table.Column<Guid>(type: "uuid", nullable: true),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReferrerHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    DeviceClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analytics_events", x => x.Id);
                    table.CheckConstraint("CK_analytics_events_DeviceClass", "\"DeviceClass\" IN (\r\n    'MOBILE',\r\n    'TABLET',\r\n    'DESKTOP',\r\n    'UNKNOWN')");
                    table.CheckConstraint("CK_analytics_events_EventType", "\"EventType\" IN (\r\n    'ASSET_VIEW',\r\n    'BUNDLE_VIEW',\r\n    'COLLECTION_VIEW',\r\n    'COLLECTION_ITEM_CLICK',\r\n    'DOWNLOAD_REQUESTED')");
                    table.CheckConstraint("CK_analytics_events_ReferrerHost_length", "\"ReferrerHost\" IS NULL\r\nOR (length(\"ReferrerHost\") > 0 AND length(\"ReferrerHost\") <= 253)");
                    table.CheckConstraint("CK_analytics_events_ReferrerHost_source", "\"ReferrerHost\" IS NULL OR \"Source\" = 'EXTERNAL'");
                    table.CheckConstraint("CK_analytics_events_Source", "\"Source\" IN (\r\n    'CATALOG',\r\n    'SEARCH',\r\n    'SELLER_PROFILE',\r\n    'COLLECTION',\r\n    'BUNDLE_PAGE',\r\n    'DIRECT_INTERNAL',\r\n    'EXTERNAL',\r\n    'UNKNOWN')");
                    table.CheckConstraint("CK_analytics_events_target_shape", "(\"EventType\" = 'ASSET_VIEW'\r\n    AND \"AssetId\" IS NOT NULL AND \"AssetVersionId\" IS NULL AND \"BundleId\" IS NULL AND \"CollectionId\" IS NULL)\r\nOR (\"EventType\" = 'BUNDLE_VIEW'\r\n    AND \"BundleId\" IS NOT NULL AND \"AssetId\" IS NULL AND \"AssetVersionId\" IS NULL AND \"CollectionId\" IS NULL)\r\nOR (\"EventType\" = 'COLLECTION_VIEW'\r\n    AND \"CollectionId\" IS NOT NULL AND \"AssetId\" IS NULL AND \"AssetVersionId\" IS NULL AND \"BundleId\" IS NULL)\r\nOR (\"EventType\" = 'COLLECTION_ITEM_CLICK'\r\n    AND \"CollectionId\" IS NOT NULL AND \"AssetId\" IS NOT NULL AND \"AssetVersionId\" IS NULL AND \"BundleId\" IS NULL)\r\nOR (\"EventType\" = 'DOWNLOAD_REQUESTED'\r\n    AND \"AssetId\" IS NOT NULL AND \"AssetVersionId\" IS NOT NULL AND \"BundleId\" IS NULL AND \"CollectionId\" IS NULL)");
                });

            migrationBuilder.CreateTable(
                name: "collection_analytics_daily",
                columns: table => new
                {
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
                    CollectionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Views = table.Column<long>(type: "bigint", nullable: false),
                    ItemClicks = table.Column<long>(type: "bigint", nullable: false),
                    UniqueVisitors = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_collection_analytics_daily", x => new { x.SellerId, x.DayUtc, x.CollectionId });
                    table.CheckConstraint("CK_collection_analytics_daily_counters_non_negative", "\"Views\" >= 0 AND \"ItemClicks\" >= 0 AND \"UniqueVisitors\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "product_analytics_daily",
                columns: table => new
                {
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
                    ProductType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    Views = table.Column<long>(type: "bigint", nullable: false),
                    DownloadRequests = table.Column<long>(type: "bigint", nullable: false),
                    UniqueVisitors = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_product_analytics_daily", x => new { x.SellerId, x.DayUtc, x.ProductType, x.ProductId });
                    table.CheckConstraint("CK_product_analytics_daily_counters_non_negative", "\"Views\" >= 0 AND \"DownloadRequests\" >= 0 AND \"UniqueVisitors\" >= 0");
                    table.CheckConstraint("CK_product_analytics_daily_ProductType", "\"ProductType\" IN ('ASSET', 'BUNDLE')");
                });

            migrationBuilder.CreateTable(
                name: "seller_analytics_daily",
                columns: table => new
                {
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
                    AssetViews = table.Column<long>(type: "bigint", nullable: false),
                    BundleViews = table.Column<long>(type: "bigint", nullable: false),
                    CollectionViews = table.Column<long>(type: "bigint", nullable: false),
                    CollectionItemClicks = table.Column<long>(type: "bigint", nullable: false),
                    DownloadRequests = table.Column<long>(type: "bigint", nullable: false),
                    UniqueVisitors = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seller_analytics_daily", x => new { x.SellerId, x.DayUtc });
                    table.CheckConstraint("CK_seller_analytics_daily_counters_non_negative", "\"AssetViews\" >= 0 AND \"BundleViews\" >= 0 AND \"CollectionViews\" >= 0\r\nAND \"CollectionItemClicks\" >= 0 AND \"DownloadRequests\" >= 0 AND \"UniqueVisitors\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "traffic_analytics_daily",
                columns: table => new
                {
                    SellerId = table.Column<Guid>(type: "uuid", nullable: false),
                    DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReferrerHostKey = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    ProductViews = table.Column<long>(type: "bigint", nullable: false),
                    UniqueVisitors = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_traffic_analytics_daily", x => new { x.SellerId, x.DayUtc, x.Source, x.ReferrerHostKey });
                    table.CheckConstraint("CK_traffic_analytics_daily_counters_non_negative", "\"ProductViews\" >= 0 AND \"UniqueVisitors\" >= 0");
                    table.CheckConstraint("CK_traffic_analytics_daily_ReferrerHostKey_external_only", "\"ReferrerHostKey\" = '' OR \"Source\" = 'EXTERNAL'");
                    table.CheckConstraint("CK_traffic_analytics_daily_Source", "\"Source\" IN (\r\n    'CATALOG',\r\n    'SEARCH',\r\n    'SELLER_PROFILE',\r\n    'COLLECTION',\r\n    'BUNDLE_PAGE',\r\n    'DIRECT_INTERNAL',\r\n    'EXTERNAL',\r\n    'UNKNOWN')");
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_checkout_intents_attribution_collection",
                table: "checkout_intents",
                sql: "(\"AttributionSource\" = 'COLLECTION'\r\n    AND \"AttributionCollectionId\" IS NOT NULL\r\n    AND \"AssetId\" IS NOT NULL\r\n    AND \"BundleId\" IS NULL)\r\nOR (\"AttributionSource\" IS DISTINCT FROM 'COLLECTION'\r\n    AND \"AttributionCollectionId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_checkout_intents_attribution_null_consistency",
                table: "checkout_intents",
                sql: "\"AttributionSource\" IS NOT NULL\r\nOR (\"AnalyticsVisitorId\" IS NULL\r\n    AND \"AnalyticsSessionId\" IS NULL\r\n    AND \"AttributionCollectionId\" IS NULL\r\n    AND \"AttributionReferrerHost\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_checkout_intents_attribution_referrer_host",
                table: "checkout_intents",
                sql: "\"AttributionReferrerHost\" IS NULL\r\nOR \"AttributionSource\" = 'EXTERNAL'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_checkout_intents_AttributionSource",
                table: "checkout_intents",
                sql: "\"AttributionSource\" IS NULL OR \"AttributionSource\" IN (\r\n    'CATALOG',\r\n    'SEARCH',\r\n    'SELLER_PROFILE',\r\n    'COLLECTION',\r\n    'BUNDLE_PAGE',\r\n    'DIRECT_INTERNAL',\r\n    'EXTERNAL',\r\n    'UNKNOWN')");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_OccurredAt_brin",
                table: "analytics_events",
                column: "OccurredAt")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_SellerId_AssetId_OccurredAt",
                table: "analytics_events",
                columns: new[] { "SellerId", "AssetId", "OccurredAt" },
                filter: "\"AssetId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_SellerId_BundleId_OccurredAt",
                table: "analytics_events",
                columns: new[] { "SellerId", "BundleId", "OccurredAt" },
                filter: "\"BundleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_SellerId_CollectionId_OccurredAt",
                table: "analytics_events",
                columns: new[] { "SellerId", "CollectionId", "OccurredAt" },
                filter: "\"CollectionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_SellerId_EventType_OccurredAt_Id",
                table: "analytics_events",
                columns: new[] { "SellerId", "EventType", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_SellerId_OccurredAt_Id",
                table: "analytics_events",
                columns: new[] { "SellerId", "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_SellerId_SessionId_OccurredAt",
                table: "analytics_events",
                columns: new[] { "SellerId", "SessionId", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_analytics_events_SellerId_VisitorId_OccurredAt",
                table: "analytics_events",
                columns: new[] { "SellerId", "VisitorId", "OccurredAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analytics_events");

            migrationBuilder.DropTable(
                name: "collection_analytics_daily");

            migrationBuilder.DropTable(
                name: "product_analytics_daily");

            migrationBuilder.DropTable(
                name: "seller_analytics_daily");

            migrationBuilder.DropTable(
                name: "traffic_analytics_daily");

            migrationBuilder.DropCheckConstraint(
                name: "CK_checkout_intents_attribution_collection",
                table: "checkout_intents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_checkout_intents_attribution_null_consistency",
                table: "checkout_intents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_checkout_intents_attribution_referrer_host",
                table: "checkout_intents");

            migrationBuilder.DropCheckConstraint(
                name: "CK_checkout_intents_AttributionSource",
                table: "checkout_intents");

            migrationBuilder.DropColumn(
                name: "AnalyticsSessionId",
                table: "checkout_intents");

            migrationBuilder.DropColumn(
                name: "AnalyticsVisitorId",
                table: "checkout_intents");

            migrationBuilder.DropColumn(
                name: "AttributionCollectionId",
                table: "checkout_intents");

            migrationBuilder.DropColumn(
                name: "AttributionReferrerHost",
                table: "checkout_intents");

            migrationBuilder.DropColumn(
                name: "AttributionSource",
                table: "checkout_intents");
        }
    }
}
