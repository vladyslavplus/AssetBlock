using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;
using Pgvector;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class Initial : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,")
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "analysis_service_registrations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    RegisteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_service_registrations", x => x.Id);
                    table.CheckConstraint("CK_analysis_service_registrations_capacity", "\"Capacity\" > 0");
                    table.CheckConstraint("CK_analysis_service_registrations_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
                });

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
                name: "audit_logs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ResourceType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    TraceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_audit_logs", x => x.Id);
                    table.CheckConstraint("CK_audit_logs_ActorType", "\"ActorType\" IN ('USER', 'SYSTEM', 'ANONYMOUS')");
                    table.CheckConstraint("CK_audit_logs_MetadataJson_Object", "\"MetadataJson\" IS NULL OR jsonb_typeof(\"MetadataJson\") = 'object'");
                    table.CheckConstraint("CK_audit_logs_Outcome", "\"Outcome\" IN ('SUCCESS', 'FAILURE', 'DENIED')");
                });

            migrationBuilder.CreateTable(
                name: "categories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    Slug = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_categories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_policy_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PolicyDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    SchemaName = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    StageMatrixJson = table.Column<string>(type: "jsonb", nullable: false),
                    VerifierMode = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    VerifierReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ToolIdentitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    LimitsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SecurityValidityJson = table.Column<string>(type: "jsonb", nullable: false),
                    ModelKey = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    IndexKey = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    ReferenceSnapshotId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_policy_versions", x => x.Id);
                    table.CheckConstraint("CK_code_analysis_policy_versions_digest", "\"PolicyDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_policy_versions_limits", "jsonb_typeof(\"LimitsJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_policy_versions_security_validity", "jsonb_typeof(\"SecurityValidityJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_policy_versions_stage_matrix", "jsonb_typeof(\"StageMatrixJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_policy_versions_tool_identities", "jsonb_typeof(\"ToolIdentitiesJson\") = 'object'");
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
                name: "outbox_email_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OutboxMessageId = table.Column<Guid>(type: "uuid", nullable: false),
                    MessageId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RecipientAddress = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateKind = table.Column<int>(type: "integer", nullable: false),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeliveredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_email_deliveries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "outbox_messages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Payload = table.Column<string>(type: "text", nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockedUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadLetteredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DeadLetterReason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ReplayCount = table.Column<int>(type: "integer", nullable: false),
                    LastReplayedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_outbox_messages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "processed_stripe_webhook_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeEventId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    EventType = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_processed_stripe_webhook_events", x => x.Id);
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
                name: "recommendation_daily",
                columns: table => new
                {
                    DayUtc = table.Column<DateOnly>(type: "date", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RankingVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImpressionCount = table.Column<long>(type: "bigint", nullable: false),
                    ClickCount = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_daily", x => new { x.DayUtc, x.SourceAssetId, x.TargetAssetId, x.RankingVersion });
                    table.CheckConstraint("CK_recommendation_daily_counters_non_negative", "\"ImpressionCount\" >= 0 AND \"ClickCount\" >= 0");
                    table.CheckConstraint("CK_recommendation_daily_RankingVersion_length", "length(\"RankingVersion\") > 0\r\nAND length(\"RankingVersion\") <= 64");
                });

            migrationBuilder.CreateTable(
                name: "recommendation_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExposureId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    SlotPosition = table.Column<int>(type: "integer", nullable: false),
                    RankingVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VisitorId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeviceClass = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_recommendation_events", x => x.Id);
                    table.CheckConstraint("CK_recommendation_events_DeviceClass", "\"DeviceClass\" IN (\r\n    'MOBILE',\r\n    'TABLET',\r\n    'DESKTOP',\r\n    'UNKNOWN')");
                    table.CheckConstraint("CK_recommendation_events_EventType", "\"EventType\" IN (\r\n    'IMPRESSION',\r\n    'CLICK')");
                    table.CheckConstraint("CK_recommendation_events_RankingVersion_length", "length(\"RankingVersion\") > 0\r\nAND length(\"RankingVersion\") <= 64");
                    table.CheckConstraint("CK_recommendation_events_SlotPosition", "\"SlotPosition\" >= 0\r\nAND \"SlotPosition\" <= 11");
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
                name: "social_platforms",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    IconName = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_social_platforms", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "storage_orphan_cleanup_states",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    CursorKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    CursorVersionId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    LeaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_storage_orphan_cleanup_states", x => x.Id);
                    table.CheckConstraint("CK_storage_orphan_cleanup_states_SingletonId", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "tags",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_tags", x => x.Id);
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

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Username = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    PasswordHash = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Role = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false, defaultValue: "User"),
                    RoleRevision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    AvatarUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Bio = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    IsPublicProfile = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    EmailVerifiedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.Id);
                });

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
                name: "email_actions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TargetEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Version = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsumedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastSentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_actions", x => x.Id);
                    table.CheckConstraint("CK_email_actions_ExpiresAt_After_CreatedAt", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_email_actions_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "json_mutation_idempotency",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationKind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestDigest = table.Column<string>(type: "char(64)", nullable: false),
                    ResultJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_json_mutation_idempotency", x => x.Id);
                    table.ForeignKey(
                        name: "FK_json_mutation_idempotency_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                    table.ForeignKey(
                        name: "FK_refresh_tokens_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_notifications",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MetadataJson = table.Column<string>(type: "character varying(8192)", maxLength: 8192, nullable: false),
                    ReadAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SourceOutboxMessageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_notifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_notifications_users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_recommendation_preferences",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsPersonalized = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    OptedInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_recommendation_preferences", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_user_recommendation_preferences_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_social_links",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlatformId = table.Column<Guid>(type: "uuid", nullable: false),
                    Url = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_social_links", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_social_links_social_platforms_PlatformId",
                        column: x => x.PlatformId,
                        principalTable: "social_platforms",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_social_links_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_source_click_affinity",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TargetAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Clicks = table.Column<long>(type: "bigint", nullable: false),
                    LastClickedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_source_click_affinity", x => new { x.UserId, x.SourceAssetId, x.TargetAssetId });
                    table.CheckConstraint("CK_user_source_click_affinity_clicks_non_negative", "\"Clicks\" >= 0");
                    table.ForeignKey(
                        name: "FK_user_source_click_affinity_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "user_tag_affinity",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false),
                    Purchases = table.Column<long>(type: "bigint", nullable: false),
                    Reviews = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_tag_affinity", x => new { x.UserId, x.TagId });
                    table.CheckConstraint("CK_user_tag_affinity_counters_non_negative", "\"Purchases\" >= 0 AND \"Reviews\" >= 0");
                    table.ForeignKey(
                        name: "FK_user_tag_affinity_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    table.CheckConstraint("CK_bundle_revisions_currency_iso_lower", "length(\"Currency\") = 3 AND \"Currency\" = lower(\"Currency\")");
                    table.CheckConstraint("CK_bundle_revisions_currency_usd_v1", "\"Currency\" = 'usd'");
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
                name: "analysis_capacity_slots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ServiceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    SlotNumber = table.Column<int>(type: "integer", nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ReservedRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReservedJobId = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseToken = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    ReservationGeneration = table.Column<int>(type: "integer", nullable: true),
                    HardDeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CleanupGraceUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConfirmedTerminationAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ReservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_analysis_capacity_slots", x => x.Id);
                    table.CheckConstraint("CK_analysis_capacity_slots_capacity", "\"Capacity\" > 0");
                    table.CheckConstraint("CK_analysis_capacity_slots_lease_token", "\"LeaseToken\" IS NULL OR \"LeaseToken\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_analysis_capacity_slots_release_reason", "(\"State\" = 'FREE' AND \"ReleasedReason\" IS NOT NULL AND length(\"ReleasedReason\") > 0) OR \"State\" <> 'FREE'");
                    table.CheckConstraint("CK_analysis_capacity_slots_reservation", "(\"State\" = 'FREE' AND \"ReservedRunId\" IS NULL AND \"ReservedJobId\" IS NULL AND \"LeaseToken\" IS NULL AND \"ReservationGeneration\" IS NULL AND \"HardDeadlineUtc\" IS NULL) OR (\"State\" <> 'FREE' AND \"ReservedRunId\" IS NOT NULL AND \"ReservedJobId\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"ReservationGeneration\" IS NOT NULL AND \"HardDeadlineUtc\" IS NOT NULL)");
                    table.CheckConstraint("CK_analysis_capacity_slots_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
                    table.CheckConstraint("CK_analysis_capacity_slots_slot_number", "\"SlotNumber\" >= 0");
                    table.CheckConstraint("CK_analysis_capacity_slots_state", "\"State\" IN ('FREE', 'RESERVED', 'RELEASING', 'RECOVERING')");
                });

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
                });

            migrationBuilder.CreateTable(
                name: "asset_draft_workspaces",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceRevision = table.Column<long>(type: "bigint", nullable: false),
                    MaterialMetadataHeadRevision = table.Column<int>(type: "integer", nullable: false),
                    SourceDeclarationHeadRevision = table.Column<int>(type: "integer", nullable: false),
                    SellerEvidenceHeadRevision = table.Column<int>(type: "integer", nullable: false),
                    CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_draft_workspaces", x => x.Id);
                    table.UniqueConstraint("AK_asset_draft_workspaces_AssetId_Id_WorkspaceVersionScopeKey", x => new { x.AssetId, x.Id, x.WorkspaceVersionScopeKey });
                    table.CheckConstraint("CK_asset_draft_workspaces_case_revision", "\"CaseRevision\" > 0");
                    table.CheckConstraint("CK_asset_draft_workspaces_revision", "\"WorkspaceRevision\" > 0");
                    table.CheckConstraint("CK_asset_draft_workspaces_version_scope_key", "((\"AssetVersionId\" IS NULL AND \"WorkspaceVersionScopeKey\" = '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"AssetVersionId\" IS NOT NULL AND \"WorkspaceVersionScopeKey\" = \"AssetVersionId\"))");
                });

            migrationBuilder.CreateTable(
                name: "asset_embeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModelKey = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    Provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ModelId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ModelRevision = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ModelDigest = table.Column<string>(type: "character varying(71)", maxLength: 71, nullable: false),
                    Dimension = table.Column<int>(type: "integer", nullable: false),
                    ContentSchemaVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceRevision = table.Column<long>(type: "bigint", nullable: false),
                    ContentHash = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(768)", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_embeddings", x => x.Id);
                    table.CheckConstraint("CK_asset_embeddings_content_hash", "\"ContentHash\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_asset_embeddings_dimension", "\"Dimension\" = 768");
                    table.CheckConstraint("CK_asset_embeddings_model_digest", "\"ModelDigest\" ~ '^sha256:[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_asset_embeddings_model_key", "\"ModelKey\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_asset_embeddings_source_revision", "\"SourceRevision\" > 0");
                    table.CheckConstraint("CK_asset_embeddings_vector_dims", "vector_dims(\"Embedding\") = \"Dimension\"");
                });

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
                });

            migrationBuilder.CreateTable(
                name: "asset_material_metadata_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentDigest = table.Column<string>(type: "char(64)", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSubmitted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_material_metadata_revisions", x => x.Id);
                    table.CheckConstraint("CK_asset_material_metadata_revisions_digest", "length(\"ContentDigest\") = 64");
                    table.CheckConstraint("CK_asset_material_metadata_revisions_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_asset_material_metadata_revisions_revision_scope", "((\"AssetVersionId\" IS NULL AND \"WorkspaceVersionScopeKey\" = '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"AssetVersionId\" IS NOT NULL AND \"WorkspaceVersionScopeKey\" = \"AssetVersionId\"))");
                    table.ForeignKey(
                        name: "FK_asset_material_metadata_revisions_asset_draft_workspaces_As~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.WorkspaceVersionScopeKey },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                });

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
                    InputHash = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    ModelKey = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    AnalysisRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    SecurityRecheckId = table.Column<Guid>(type: "uuid", nullable: true),
                    TransportLeaseToken = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_processing_jobs", x => x.Id);
                    table.CheckConstraint("CK_asset_processing_jobs_attempt_count", "\"AttemptCount\" >= 0 AND \"AttemptCount\" <= \"MaxAttempts\"");
                    table.CheckConstraint("CK_asset_processing_jobs_definition_version", "\"DefinitionVersion\" > 0");
                    table.CheckConstraint("CK_asset_processing_jobs_embedding_hashes", "(\"Type\" = 'EMBEDDING_GENERATION' AND \"InputHash\" IS NOT NULL AND \"InputHash\" ~ '^[0-9a-f]{64}$' AND \"ModelKey\" IS NOT NULL AND \"ModelKey\" ~ '^[0-9a-f]{64}$') OR (\"Type\" <> 'EMBEDDING_GENERATION' AND \"InputHash\" IS NULL AND \"ModelKey\" IS NULL)");
                    table.CheckConstraint("CK_asset_processing_jobs_error_code", "\"ErrorCode\" IS NULL OR \"ErrorCode\" ~ '^[A-Z0-9_]{1,64}$'");
                    table.CheckConstraint("CK_asset_processing_jobs_max_attempts", "\"MaxAttempts\" > 0 AND \"MaxAttempts\" <= 10");
                    table.CheckConstraint("CK_asset_processing_jobs_payload_size", "octet_length(CAST(\"Payload\" AS text)) <= 4000");
                    table.CheckConstraint("CK_asset_processing_jobs_payload_type", "jsonb_typeof(\"Payload\") = 'object'");
                    table.CheckConstraint("CK_asset_processing_jobs_result_size", "\"Result\" IS NULL OR octet_length(CAST(\"Result\" AS text)) <= 4000");
                    table.CheckConstraint("CK_asset_processing_jobs_result_type", "\"Result\" IS NULL OR jsonb_typeof(\"Result\") = 'object'");
                    table.CheckConstraint("CK_asset_processing_jobs_running_lease", "(\"Status\" = 'RUNNING' AND \"LeaseOwner\" IS NOT NULL AND \"LeaseToken\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL) OR (\"Status\" != 'RUNNING' AND \"LeaseOwner\" IS NULL AND \"LeaseToken\" IS NULL AND \"LeaseExpiresAt\" IS NULL)");
                    table.CheckConstraint("CK_asset_processing_jobs_scope_exclusivity", "\"Type\" NOT IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN', 'LISTING_COPILOT', 'EMBEDDING_GENERATION', 'CODE_ANALYSIS') OR ((\"Type\" = 'CODE_ANALYSIS' AND \"AnalysisRunId\" IS NOT NULL AND \"SecurityRecheckId\" IS NULL) OR (\"Type\" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN') AND \"AnalysisRunId\" IS NULL) OR (\"Type\" IN ('LISTING_COPILOT', 'EMBEDDING_GENERATION') AND \"AnalysisRunId\" IS NULL AND \"SecurityRecheckId\" IS NULL))");
                    table.CheckConstraint("CK_asset_processing_jobs_status", "\"Status\" IN ('QUEUED', 'RUNNING', 'RETRY_SCHEDULED', 'SUCCEEDED', 'FAILED', 'CANCELLED')");
                    table.CheckConstraint("CK_asset_processing_jobs_terminal_completed_at", "(\"Status\" IN ('SUCCEEDED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" NOT IN ('SUCCEEDED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NULL)");
                    table.CheckConstraint("CK_asset_processing_jobs_transport_lease_token", "\"TransportLeaseToken\" IS NULL OR \"TransportLeaseToken\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_asset_processing_jobs_type", "\"Type\" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN', 'LISTING_COPILOT', 'EMBEDDING_GENERATION', 'CODE_ANALYSIS')");
                });

            migrationBuilder.CreateTable(
                name: "asset_seller_evidence_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentDigest = table.Column<string>(type: "char(64)", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSubmitted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_seller_evidence_revisions", x => x.Id);
                    table.UniqueConstraint("AK_asset_seller_evidence_revisions_WorkspaceId_Revision", x => new { x.WorkspaceId, x.Revision });
                    table.CheckConstraint("CK_asset_seller_evidence_revisions_digest", "length(\"ContentDigest\") = 64");
                    table.CheckConstraint("CK_asset_seller_evidence_revisions_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_asset_seller_evidence_revisions_revision_scope", "((\"AssetVersionId\" IS NULL AND \"WorkspaceVersionScopeKey\" = '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"AssetVersionId\" IS NOT NULL AND \"WorkspaceVersionScopeKey\" = \"AssetVersionId\"))");
                    table.ForeignKey(
                        name: "FK_asset_seller_evidence_revisions_asset_draft_workspaces_Asse~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.WorkspaceVersionScopeKey },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_source_declaration_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<int>(type: "integer", nullable: false),
                    AuthorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentDigest = table.Column<string>(type: "char(64)", nullable: false),
                    SchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsSubmitted = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_source_declaration_revisions", x => x.Id);
                    table.CheckConstraint("CK_asset_source_declaration_revisions_digest", "length(\"ContentDigest\") = 64");
                    table.CheckConstraint("CK_asset_source_declaration_revisions_revision", "\"Revision\" > 0");
                    table.CheckConstraint("CK_asset_source_declaration_revisions_revision_scope", "((\"AssetVersionId\" IS NULL AND \"WorkspaceVersionScopeKey\" = '00000000-0000-0000-0000-000000000000'::uuid)\r\nOR (\"AssetVersionId\" IS NOT NULL AND \"WorkspaceVersionScopeKey\" = \"AssetVersionId\"))");
                    table.ForeignKey(
                        name: "FK_asset_source_declaration_revisions_asset_draft_workspaces_A~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.WorkspaceVersionScopeKey },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "asset_tags",
                columns: table => new
                {
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    TagId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_tags", x => new { x.AssetId, x.TagId });
                    table.ForeignKey(
                        name: "FK_asset_tags_tags_TagId",
                        column: x => x.TagId,
                        principalTable: "tags",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "asset_versions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    IsCurrent = table.Column<bool>(type: "boolean", nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    StorageVersionId = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true),
                    StorageRetiredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    FileName = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    ContentLength = table.Column<long>(type: "bigint", nullable: false),
                    ContentSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReleaseNotes = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    LicenseCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LicenseTemplateVersion = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    LicenseDisplayName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    LicenseTerms = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    ProcessingStatus = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProcessingErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProcessingErrorSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    ProcessingUpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SafetyRestriction = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false, defaultValue: "NONE"),
                    SafetyRestrictionReason = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SafetyRestrictedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SelectedCodeAnalysisReportId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnalysisSelectionGeneration = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_versions", x => x.Id);
                    table.UniqueConstraint("AK_asset_versions_AssetId_Id", x => new { x.AssetId, x.Id });
                    table.CheckConstraint("CK_asset_versions_analysis_selection_generation", "\"AnalysisSelectionGeneration\" > 0");
                    table.CheckConstraint("CK_asset_versions_content_length_positive", "\"ContentLength\" > 0");
                    table.CheckConstraint("CK_asset_versions_processing_error_code", "\"ProcessingErrorCode\" IS NULL OR \"ProcessingErrorCode\" ~ '^[A-Z0-9_]{1,64}$'");
                    table.CheckConstraint("CK_asset_versions_processing_status", "\"ProcessingStatus\" IN ('PENDING_INSPECTION', 'PENDING_MALWARE_SCAN', 'READY', 'REJECTED', 'PROCESSING_FAILED')");
                    table.CheckConstraint("CK_asset_versions_ready_current", "\"IsCurrent\" = false OR (\"IsCurrent\" = true AND \"ProcessingStatus\" = 'READY')");
                    table.CheckConstraint("CK_asset_versions_safety_restriction", "\"SafetyRestriction\" IN ('NONE', 'UNSAFE_ARCHIVE', 'INFECTED', 'INTEGRITY_MISMATCH')");
                    table.CheckConstraint("CK_asset_versions_safety_restriction_reason", "(\"SafetyRestriction\" = 'NONE' AND \"SafetyRestrictionReason\" IS NULL AND \"SafetyRestrictedAt\" IS NULL) OR (\"SafetyRestriction\" <> 'NONE' AND \"SafetyRestrictionReason\" IS NOT NULL AND \"SafetyRestrictedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_asset_versions_state_error_consistency", "(\"ProcessingStatus\" IN ('PENDING_INSPECTION', 'PENDING_MALWARE_SCAN', 'READY') AND \"ProcessingErrorCode\" IS NULL AND \"ProcessingErrorSummary\" IS NULL) OR (\"ProcessingStatus\" IN ('REJECTED', 'PROCESSING_FAILED') AND \"ProcessingErrorCode\" IS NOT NULL AND \"ProcessingErrorSummary\" IS NOT NULL AND length(trim(\"ProcessingErrorSummary\")) > 0)");
                    table.CheckConstraint("CK_asset_versions_version_number_positive", "\"VersionNumber\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "assets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AuthorId = table.Column<Guid>(type: "uuid", nullable: false),
                    CategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Description = table.Column<string>(type: "character varying(5000)", maxLength: 5000, nullable: true),
                    Price = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    DownloadLimitPerHour = table.Column<int>(type: "integer", nullable: true),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RatingAverage = table.Column<double>(type: "double precision", nullable: false, defaultValue: 0.0),
                    RatingCount = table.Column<int>(type: "integer", nullable: false, defaultValue: 0),
                    SearchRevision = table.Column<long>(type: "bigint", nullable: false, defaultValue: 1L),
                    CurrentPublicationSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "to_tsvector('simple'::regconfig, coalesce(\"Title\", '') || ' ' || coalesce(\"Description\", ''))", stored: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.Id);
                    table.CheckConstraint("CK_assets_search_revision", "\"SearchRevision\" > 0");
                    table.ForeignKey(
                        name: "FK_assets_categories_CategoryId",
                        column: x => x.CategoryId,
                        principalTable: "categories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_assets_users_AuthorId",
                        column: x => x.AuthorId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "checkout_intents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: true),
                    BundleId = table.Column<Guid>(type: "uuid", nullable: true),
                    BundleRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ProductTitle = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    AmountTotal = table.Column<decimal>(type: "numeric(18,2)", precision: 18, scale: 2, nullable: false),
                    Currency = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    StripeSessionId = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastStripeReconciledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AnalyticsVisitorId = table.Column<Guid>(type: "uuid", nullable: true),
                    AnalyticsSessionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttributionSource = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    AttributionCollectionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AttributionReferrerHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_intents", x => x.Id);
                    table.CheckConstraint("CK_checkout_intents_amount_total_positive", "\"AmountTotal\" > 0");
                    table.CheckConstraint("CK_checkout_intents_attribution_collection", "(\"AttributionSource\" = 'COLLECTION'\r\n    AND \"AttributionCollectionId\" IS NOT NULL\r\n    AND \"AssetId\" IS NOT NULL\r\n    AND \"BundleId\" IS NULL)\r\nOR (\"AttributionSource\" IS DISTINCT FROM 'COLLECTION'\r\n    AND \"AttributionCollectionId\" IS NULL)");
                    table.CheckConstraint("CK_checkout_intents_attribution_null_consistency", "\"AttributionSource\" IS NOT NULL\r\nOR (\"AnalyticsVisitorId\" IS NULL\r\n    AND \"AnalyticsSessionId\" IS NULL\r\n    AND \"AttributionCollectionId\" IS NULL\r\n    AND \"AttributionReferrerHost\" IS NULL)");
                    table.CheckConstraint("CK_checkout_intents_attribution_referrer_host", "\"AttributionReferrerHost\" IS NULL\r\nOR \"AttributionSource\" = 'EXTERNAL'");
                    table.CheckConstraint("CK_checkout_intents_AttributionSource", "\"AttributionSource\" IS NULL OR \"AttributionSource\" IN (\r\n    'CATALOG',\r\n    'SEARCH',\r\n    'SELLER_PROFILE',\r\n    'COLLECTION',\r\n    'BUNDLE_PAGE',\r\n    'DIRECT_INTERNAL',\r\n    'EXTERNAL',\r\n    'UNKNOWN')");
                    table.CheckConstraint("CK_checkout_intents_currency_iso_lower", "length(\"Currency\") = 3 AND \"Currency\" = lower(\"Currency\")");
                    table.CheckConstraint("CK_checkout_intents_currency_usd_v1", "\"Currency\" = 'usd'");
                    table.CheckConstraint("CK_checkout_intents_exactly_one_product", "(\"AssetId\" IS NOT NULL AND \"BundleId\" IS NULL AND \"BundleRevisionId\" IS NULL)\r\nOR (\"AssetId\" IS NULL AND \"BundleId\" IS NOT NULL AND \"BundleRevisionId\" IS NOT NULL)");
                    table.CheckConstraint("CK_checkout_intents_expires_after_created", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.ForeignKey(
                        name: "FK_checkout_intents_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_checkout_intents_bundle_revisions_BundleRevisionId",
                        column: x => x.BundleRevisionId,
                        principalTable: "bundle_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_checkout_intents_bundles_BundleId",
                        column: x => x.BundleId,
                        principalTable: "bundles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_checkout_intents_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
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
                name: "reviews",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Rating = table.Column<int>(type: "integer", nullable: false),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_reviews", x => x.Id);
                    table.ForeignKey(
                        name: "FK_reviews_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_reviews_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    table.CheckConstraint("CK_orders_currency_iso_lower", "length(\"Currency\") = 3 AND \"Currency\" = lower(\"Currency\")");
                    table.CheckConstraint("CK_orders_currency_usd_v1", "\"Currency\" = 'usd'");
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
                name: "paid_checkout_reconciliation_holds",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CheckoutIntentId = table.Column<Guid>(type: "uuid", nullable: false),
                    StripeSessionId = table.Column<string>(type: "text", nullable: true),
                    StripeEventId = table.Column<string>(type: "text", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    SafePaymentFactsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ItemIdentitiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_paid_checkout_reconciliation_holds", x => x.Id);
                    table.ForeignKey(
                        name: "FK_paid_checkout_reconciliation_holds_checkout_intents_Checkou~",
                        column: x => x.CheckoutIntentId,
                        principalTable: "checkout_intents",
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
                    LicenseTerms = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    PublicationSnapshotId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_checkout_intent_items", x => x.Id);
                    table.CheckConstraint("CK_checkout_intent_items_allocated_price_positive", "\"AllocatedPrice\" > 0");
                    table.CheckConstraint("CK_checkout_intent_items_list_price_positive", "\"ListPrice\" > 0");
                    table.CheckConstraint("CK_checkout_intent_items_position_positive", "\"Position\" > 0");
                    table.CheckConstraint("CK_checkout_intent_items_version_positive", "\"VersionNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_checkout_intent_items_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
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
                name: "code_analysis_report_admissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    AdmissionKind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AdmittedBy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AdmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReportDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    PythonReportId = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    InputDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    ServiceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ReservationSlotNumber = table.Column<int>(type: "integer", nullable: false),
                    ReservationGeneration = table.Column<int>(type: "integer", nullable: false),
                    ReservationLeaseTokenDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    ReviewEligible = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewEligibilityReason = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_report_admissions", x => x.Id);
                    table.CheckConstraint("CK_code_analysis_report_admissions_generation", "\"ReservationGeneration\" > 0");
                    table.CheckConstraint("CK_code_analysis_report_admissions_input_digest", "\"InputDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_kind", "\"AdmissionKind\" = 'SERVER_VALIDATED'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_lease_token_digest", "\"ReservationLeaseTokenDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_python_report_id", "\"PythonReportId\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_report_digest", "\"ReportDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_review_reason", "\"ReviewEligibilityReason\" IS NOT NULL AND length(\"ReviewEligibilityReason\") > 0");
                    table.CheckConstraint("CK_code_analysis_report_admissions_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
                    table.CheckConstraint("CK_code_analysis_report_admissions_slot_number", "\"ReservationSlotNumber\" >= 0");
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_report_findings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    FindingType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    OwnScopePath = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    OwnExcerpt = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false, defaultValue: ""),
                    OwnStartByte = table.Column<int>(type: "integer", nullable: true),
                    OwnEndByte = table.Column<int>(type: "integer", nullable: true),
                    OwnStartLine = table.Column<int>(type: "integer", nullable: true),
                    OwnEndLine = table.Column<int>(type: "integer", nullable: true),
                    ForeignSourceId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    ForeignOrigin = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ForeignSensitivity = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    PublicAttributionJson = table.Column<string>(type: "jsonb", nullable: true),
                    TokenScore = table.Column<double>(type: "double precision", nullable: true),
                    SemanticScore = table.Column<double>(type: "double precision", nullable: true),
                    TokenRank = table.Column<int>(type: "integer", nullable: true),
                    SemanticRank = table.Column<int>(type: "integer", nullable: true),
                    RetrievalEvidenceJson = table.Column<string>(type: "jsonb", nullable: true),
                    ReasonText = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                    RequestedAction = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_report_findings", x => x.Id);
                    table.UniqueConstraint("AK_code_analysis_report_findings_ReportId_Id", x => new { x.ReportId, x.Id });
                    table.CheckConstraint("CK_code_analysis_report_findings_attribution", "\"ForeignSensitivity\" <> 'PRIVATE' OR \"PublicAttributionJson\" IS NULL");
                    table.CheckConstraint("CK_code_analysis_report_findings_attribution_json", "\"PublicAttributionJson\" IS NULL OR jsonb_typeof(\"PublicAttributionJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_report_findings_key", "\"FindingKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
                    table.CheckConstraint("CK_code_analysis_report_findings_lines", "(\"OwnStartLine\" IS NULL AND \"OwnEndLine\" IS NULL) OR (\"OwnStartLine\" IS NOT NULL AND \"OwnEndLine\" IS NOT NULL AND \"OwnStartLine\" > 0 AND \"OwnEndLine\" >= \"OwnStartLine\")");
                    table.CheckConstraint("CK_code_analysis_report_findings_origin", "\"ForeignOrigin\" IS NULL OR \"ForeignOrigin\" IN ('OWN', 'FOREIGN')");
                    table.CheckConstraint("CK_code_analysis_report_findings_scores", "\"TokenScore\" IS NULL OR \"TokenScore\" <> 'NaN'::double precision");
                    table.CheckConstraint("CK_code_analysis_report_findings_scores_semantic", "\"SemanticScore\" IS NULL OR \"SemanticScore\" <> 'NaN'::double precision");
                    table.CheckConstraint("CK_code_analysis_report_findings_sensitivity", "\"ForeignSensitivity\" IN ('PUBLIC', 'PRIVATE')");
                    table.CheckConstraint("CK_code_analysis_report_findings_spans", "(\"OwnStartByte\" IS NULL AND \"OwnEndByte\" IS NULL) OR (\"OwnStartByte\" IS NOT NULL AND \"OwnEndByte\" IS NOT NULL AND \"OwnStartByte\" >= 0 AND \"OwnEndByte\" >= \"OwnStartByte\")");
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_report_headers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputRevision = table.Column<int>(type: "integer", nullable: false),
                    Purpose = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ReviewEligible = table.Column<bool>(type: "boolean", nullable: false),
                    IsFinalized = table.Column<bool>(type: "boolean", nullable: false),
                    FinalizedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReportSchemaVersion = table.Column<int>(type: "integer", nullable: false),
                    AnalysisRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    PythonReportId = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    ReportDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    SelectionGeneration = table.Column<int>(type: "integer", nullable: true),
                    CoverageLevel = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    AnalyzedFileCount = table.Column<int>(type: "integer", nullable: true),
                    OmissionCount = table.Column<int>(type: "integer", nullable: true),
                    FindingCount = table.Column<int>(type: "integer", nullable: true),
                    Disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    RetrievalSummaryJson = table.Column<string>(type: "jsonb", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_report_headers", x => x.Id);
                    table.UniqueConstraint("AK_code_analysis_report_headers_AssetId_AssetVersionId_Id", x => new { x.AssetId, x.AssetVersionId, x.Id });
                    table.CheckConstraint("CK_code_analysis_report_headers_counts", "(\"AnalyzedFileCount\" IS NULL OR \"AnalyzedFileCount\" >= 0) AND (\"OmissionCount\" IS NULL OR \"OmissionCount\" >= 0) AND (\"FindingCount\" IS NULL OR \"FindingCount\" >= 0)");
                    table.CheckConstraint("CK_code_analysis_report_headers_coverage", "\"CoverageLevel\" IS NULL OR \"CoverageLevel\" IN ('FULL', 'PARTIAL', 'UNSUPPORTED')");
                    table.CheckConstraint("CK_code_analysis_report_headers_disposition", "\"Disposition\" IS NULL OR \"Disposition\" IN ('SELECTED', 'HISTORICAL_SUPERSEDED', 'HISTORICAL_STALE_INPUT', 'HISTORICAL_INCOMPLETE')");
                    table.CheckConstraint("CK_code_analysis_report_headers_python_report_id", "\"PythonReportId\" IS NULL OR \"PythonReportId\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_headers_report_digest", "\"ReportDigest\" IS NULL OR \"ReportDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_headers_selection_generation", "\"SelectionGeneration\" IS NULL OR \"SelectionGeneration\" > 0");
                    table.CheckConstraint("CK_code_analysis_report_headers_sha", "length(\"ContentSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_code_analysis_report_headers_asset_versions_AssetId_AssetVe~",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_report_headers_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "coverage_review_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", nullable: false),
                    DeclarationRevision = table.Column<int>(type: "integer", nullable: false),
                    MaterialRevision = table.Column<int>(type: "integer", nullable: false),
                    SellerEvidenceRevision = table.Column<int>(type: "integer", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    PolicyDigest = table.Column<string>(type: "char(64)", nullable: false),
                    TupleDigest = table.Column<string>(type: "char(64)", nullable: false),
                    RequestedWorkspaceRevision = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coverage_review_requests", x => x.Id);
                    table.CheckConstraint("CK_coverage_requests_digests", "length(\"TupleDigest\") = 64 AND length(\"ContentSha256\") = 64 AND length(\"PolicyDigest\") = 64");
                    table.CheckConstraint("CK_coverage_requests_revision", "\"RequestedWorkspaceRevision\" > 0 AND \"Revision\" > 0 AND \"DeclarationRevision\" > 0 AND \"MaterialRevision\" > 0 AND \"SellerEvidenceRevision\" >= 0");
                    table.ForeignKey(
                        name: "FK_coverage_review_requests_asset_draft_workspaces_AssetId_Wor~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.AssetVersionId },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coverage_review_requests_asset_versions_AssetId_AssetVersio~",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coverage_review_requests_code_analysis_report_headers_Asset~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.ReportId },
                        principalTable: "code_analysis_report_headers",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coverage_review_requests_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "seller_responses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    SellerEvidenceRevision = table.Column<int>(type: "integer", nullable: false),
                    ResponseType = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Text = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_seller_responses", x => x.Id);
                    table.CheckConstraint("CK_seller_responses_revision", "\"SellerEvidenceRevision\" > 0");
                    table.CheckConstraint("CK_seller_responses_scope", "\"AssetVersionId\" = \"WorkspaceVersionScopeKey\"");
                    table.CheckConstraint("CK_seller_responses_text", "char_length(\"Text\") <= 2000 AND btrim(\"Text\") = \"Text\" AND (\"ResponseType\" = 'NO_EVIDENCE' OR char_length(\"Text\") > 0)");
                    table.CheckConstraint("CK_seller_responses_type", "\"ResponseType\" IN ('PRIOR_WORK','PERMITTED_COMPONENT','INDEPENDENT_IMPLEMENTATION','WRONG_SOURCE','CORRECTED_FILES','NO_EVIDENCE')");
                    table.ForeignKey(
                        name: "FK_seller_responses_asset_draft_workspaces_AssetId_WorkspaceId~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.WorkspaceVersionScopeKey },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_responses_asset_seller_evidence_revisions_WorkspaceI~",
                        columns: x => new { x.WorkspaceId, x.SellerEvidenceRevision },
                        principalTable: "asset_seller_evidence_revisions",
                        principalColumns: new[] { "WorkspaceId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_responses_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_responses_code_analysis_report_findings_ReportId_Fin~",
                        columns: x => new { x.ReportId, x.FindingId },
                        principalTable: "code_analysis_report_findings",
                        principalColumns: new[] { "ReportId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_responses_code_analysis_report_headers_AssetId_Asset~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.ReportId },
                        principalTable: "code_analysis_report_headers",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_seller_responses_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "coverage_assessments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CompetenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    ScopeJson = table.Column<string>(type: "jsonb", nullable: false),
                    ManageableScope = table.Column<bool>(type: "boolean", nullable: false),
                    ReviewPlan = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CompletedPlanReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_coverage_assessments", x => x.Id);
                    table.CheckConstraint("CK_coverage_assessments_outcome", "\"Outcome\" IN ('ACCEPTED','BLOCKED')");
                    table.CheckConstraint("CK_coverage_assessments_plan", "\"Outcome\" <> 'ACCEPTED' OR (\"ManageableScope\" = TRUE AND length(\"CompletedPlanReference\") > 0)");
                    table.CheckConstraint("CK_coverage_assessments_text", "length(\"Rationale\") BETWEEN 1 AND 2000 AND length(\"ReviewPlan\") BETWEEN 1 AND 2000 AND length(\"CompletedPlanReference\") <= 200");
                    table.ForeignKey(
                        name: "FK_coverage_assessments_coverage_review_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "coverage_review_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_coverage_assessments_users_ModeratorUserId",
                        column: x => x.ModeratorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_report_stages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Applicability = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Retryable = table.Column<bool>(type: "boolean", nullable: false),
                    DurationMilliseconds = table.Column<int>(type: "integer", nullable: false),
                    InputSha256 = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    OutputSha256 = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    ToolIdentityJson = table.Column<string>(type: "jsonb", nullable: false),
                    CountsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReuseParentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReuseParentStageId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_report_stages", x => x.Id);
                    table.CheckConstraint("CK_code_analysis_report_stages_applicability", "\"Applicability\" IN ('MANDATORY', 'OPTIONAL')");
                    table.CheckConstraint("CK_code_analysis_report_stages_counts", "jsonb_typeof(\"CountsJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_report_stages_duration", "\"DurationMilliseconds\" >= 0");
                    table.CheckConstraint("CK_code_analysis_report_stages_input_sha", "\"InputSha256\" IS NULL OR \"InputSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_stages_outcome", "\"Outcome\" IN ('SUCCEEDED', 'FAILED', 'NOT_APPLICABLE')");
                    table.CheckConstraint("CK_code_analysis_report_stages_output_sha", "\"OutputSha256\" IS NULL OR \"OutputSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_report_stages_stage", "\"Stage\" ~ '^[A-Z][A-Z0-9_]{0,31}$'");
                    table.CheckConstraint("CK_code_analysis_report_stages_tool_identity", "jsonb_typeof(\"ToolIdentityJson\") = 'object'");
                    table.ForeignKey(
                        name: "FK_code_analysis_report_stages_code_analysis_report_headers_Re~",
                        column: x => x.ReportId,
                        principalTable: "code_analysis_report_headers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_report_stages_code_analysis_report_stages_Reu~",
                        column: x => x.ReuseParentStageId,
                        principalTable: "code_analysis_report_stages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_reuse_artifacts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceStageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Stage = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    ArtifactDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    ArtifactJson = table.Column<string>(type: "jsonb", nullable: false),
                    PayloadBytes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_reuse_artifacts", x => x.Id);
                    table.CheckConstraint("CK_code_analysis_reuse_artifacts_digest", "\"ArtifactDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_reuse_artifacts_payload", "jsonb_typeof(\"ArtifactJson\") = 'object'");
                    table.CheckConstraint("CK_code_analysis_reuse_artifacts_size", "\"PayloadBytes\" > 0 AND octet_length(CAST(\"ArtifactJson\" AS text)) <= 262144");
                    table.CheckConstraint("CK_code_analysis_reuse_artifacts_stage", "\"Stage\" ~ '^[A-Z][A-Z0-9_]{0,31}$'");
                    table.ForeignKey(
                        name: "FK_code_analysis_reuse_artifacts_code_analysis_report_headers_~",
                        column: x => x.SourceReportId,
                        principalTable: "code_analysis_report_headers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_reuse_artifacts_code_analysis_report_stages_S~",
                        column: x => x.SourceStageId,
                        principalTable: "code_analysis_report_stages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "code_analysis_runs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetDraftWorkspaceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContentSha256 = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    PolicyVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    DeclarationRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeclarationDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    MaterialRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    MaterialDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    InputDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    SelectionGeneration = table.Column<int>(type: "integer", nullable: false),
                    ParentRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    SecurityRecheckId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArchiveEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    MalwareEvidenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SecurityGeneration = table.Column<int>(type: "integer", nullable: false),
                    ServiceKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TransportLeaseToken = table.Column<string>(type: "char(64)", fixedLength: true, nullable: true),
                    ReservationGeneration = table.Column<int>(type: "integer", nullable: true),
                    HardDeadlineUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Disposition = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    OwnerRetryable = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_code_analysis_runs", x => x.Id);
                    table.UniqueConstraint("AK_code_analysis_runs_AssetId_AssetVersionId_Id", x => new { x.AssetId, x.AssetVersionId, x.Id });
                    table.CheckConstraint("CK_code_analysis_runs_content_sha", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_runs_declaration_digest", "\"DeclarationDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_runs_disposition", "\"Disposition\" IS NULL OR \"Disposition\" IN ('SELECTED', 'HISTORICAL_SUPERSEDED', 'HISTORICAL_STALE_INPUT', 'HISTORICAL_INCOMPLETE')");
                    table.CheckConstraint("CK_code_analysis_runs_input_digest", "\"InputDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_runs_material_digest", "\"MaterialDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_runs_policy_digest", "\"PolicyDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_code_analysis_runs_reservation", "((\"TransportLeaseToken\" IS NULL AND \"ReservationGeneration\" IS NULL AND \"HardDeadlineUtc\" IS NULL) OR (\"TransportLeaseToken\" IS NOT NULL AND \"ReservationGeneration\" IS NOT NULL AND \"HardDeadlineUtc\" IS NOT NULL))");
                    table.CheckConstraint("CK_code_analysis_runs_security_generation", "\"SecurityGeneration\" > 0");
                    table.CheckConstraint("CK_code_analysis_runs_selection_generation", "\"SelectionGeneration\" > 0");
                    table.CheckConstraint("CK_code_analysis_runs_service_key", "\"ServiceKey\" ~ '^[A-Za-z0-9][A-Za-z0-9_.\\-]{0,127}$'");
                    table.CheckConstraint("CK_code_analysis_runs_state", "\"State\" IN ('QUEUED', 'RUNNING', 'COMPLETED', 'FAILED', 'CANCELLED')");
                    table.CheckConstraint("CK_code_analysis_runs_terminal_completed_at", "(\"State\" IN ('COMPLETED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NOT NULL) OR (\"State\" NOT IN ('COMPLETED', 'FAILED', 'CANCELLED') AND \"CompletedAt\" IS NULL)");
                    table.CheckConstraint("CK_code_analysis_runs_transport_lease_token", "\"TransportLeaseToken\" IS NULL OR \"TransportLeaseToken\" ~ '^[0-9a-f]{64}$'");
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_asset_draft_workspaces_AssetDraftWorkspa~",
                        column: x => x.AssetDraftWorkspaceId,
                        principalTable: "asset_draft_workspaces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_asset_material_metadata_revisions_Materi~",
                        column: x => x.MaterialRevisionId,
                        principalTable: "asset_material_metadata_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_asset_processing_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "asset_processing_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_asset_source_declaration_revisions_Decla~",
                        column: x => x.DeclarationRevisionId,
                        principalTable: "asset_source_declaration_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_code_analysis_policy_versions_PolicyVers~",
                        column: x => x.PolicyVersionId,
                        principalTable: "code_analysis_policy_versions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_code_analysis_runs_code_analysis_runs_ParentRunId",
                        column: x => x.ParentRunId,
                        principalTable: "code_analysis_runs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "moderation_decisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    PublicationSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    Message = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    ManualPlanCompletionReference = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_decisions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "moderation_submissions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false, defaultValue: "STANDARD"),
                    AppealOfDecisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AppealRationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkspaceVersionScopeKey = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", nullable: false),
                    CodeAnalysisReportHeaderId = table.Column<Guid>(type: "uuid", nullable: true),
                    CoverageAssessmentId = table.Column<Guid>(type: "uuid", nullable: true),
                    DeclarationRevision = table.Column<int>(type: "integer", nullable: false),
                    MaterialMetadataRevision = table.Column<int>(type: "integer", nullable: false),
                    SellerEvidenceRevision = table.Column<int>(type: "integer", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SellerEvidenceDigest = table.Column<string>(type: "char(64)", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WithdrawalReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    ClaimHolderId = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PreviousSubmissionId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_submissions", x => x.Id);
                    table.UniqueConstraint("AK_moderation_submissions_AssetId_AssetVersionId_Id", x => new { x.AssetId, x.AssetVersionId, x.Id });
                    table.CheckConstraint("CK_moderation_submissions_appeal", "(\"Kind\" = 'STANDARD' AND \"AppealOfDecisionId\" IS NULL AND \"AppealRationale\" IS NULL) OR (\"Kind\" = 'APPEAL' AND \"AppealOfDecisionId\" IS NOT NULL AND \"AppealRationale\" IS NOT NULL AND length(trim(\"AppealRationale\")) BETWEEN 1 AND 2000)");
                    table.CheckConstraint("CK_moderation_submissions_case_revision", "\"CaseRevision\" > 0");
                    table.CheckConstraint("CK_moderation_submissions_claim", "(\"State\" = 'IN_REVIEW' AND \"ClaimHolderId\" IS NOT NULL AND \"ClaimedAt\" IS NOT NULL) OR (\"State\" <> 'IN_REVIEW' AND \"ClaimHolderId\" IS NULL AND \"ClaimedAt\" IS NULL)");
                    table.CheckConstraint("CK_moderation_submissions_content_sha", "length(\"ContentSha256\") = 64");
                    table.CheckConstraint("CK_moderation_submissions_evidence_digest", "length(\"SellerEvidenceDigest\") = 64");
                    table.CheckConstraint("CK_moderation_submissions_workspace_scope", "\"AssetVersionId\" = \"WorkspaceVersionScopeKey\"");
                    table.ForeignKey(
                        name: "FK_moderation_submissions_asset_draft_workspaces_AssetId_Works~",
                        columns: x => new { x.AssetId, x.WorkspaceId, x.WorkspaceVersionScopeKey },
                        principalTable: "asset_draft_workspaces",
                        principalColumns: new[] { "AssetId", "Id", "WorkspaceVersionScopeKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_code_analysis_report_headers_AssetId~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.CodeAnalysisReportHeaderId },
                        principalTable: "code_analysis_report_headers",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_coverage_assessments_CoverageAssessm~",
                        column: x => x.CoverageAssessmentId,
                        principalTable: "coverage_assessments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_moderation_decisions_AppealOfDecisio~",
                        column: x => x.AppealOfDecisionId,
                        principalTable: "moderation_decisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_moderation_submissions_AssetId_Asset~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.PreviousSubmissionId },
                        principalTable: "moderation_submissions",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_submissions_users_ClaimHolderId",
                        column: x => x.ClaimHolderId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "moderation_finding_dispositions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReportId = table.Column<Guid>(type: "uuid", nullable: false),
                    FindingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModeratorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    Disposition = table.Column<string>(type: "character varying(48)", maxLength: 48, nullable: false),
                    Rationale = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_finding_dispositions", x => x.Id);
                    table.CheckConstraint("CK_finding_dispositions_rationale", "char_length(\"Rationale\") BETWEEN 1 AND 2000 AND btrim(\"Rationale\") = \"Rationale\"");
                    table.CheckConstraint("CK_finding_dispositions_revision", "\"CaseRevision\" > 0");
                    table.CheckConstraint("CK_finding_dispositions_type", "\"Disposition\" IN ('EXPLAINED','FALSE_MATCH','ACCEPTED_WITH_LIMITS','CHANGES_NEEDED','INSUFFICIENT_EVIDENCE')");
                    table.ForeignKey(
                        name: "FK_moderation_finding_dispositions_code_analysis_report_findin~",
                        columns: x => new { x.ReportId, x.FindingId },
                        principalTable: "code_analysis_report_findings",
                        principalColumns: new[] { "ReportId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_finding_dispositions_code_analysis_report_header~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.ReportId },
                        principalTable: "code_analysis_report_headers",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_finding_dispositions_moderation_submissions_Asse~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.SubmissionId },
                        principalTable: "moderation_submissions",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_moderation_finding_dispositions_users_ModeratorUserId",
                        column: x => x.ModeratorUserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "moderation_submission_history",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    WithdrawalReason = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    CaseRevision = table.Column<long>(type: "bigint", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_moderation_submission_history", x => x.Id);
                    table.ForeignKey(
                        name: "FK_moderation_submission_history_moderation_submissions_Submis~",
                        column: x => x.SubmissionId,
                        principalTable: "moderation_submissions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "publication_snapshots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", nullable: false),
                    CodeAnalysisReportHeaderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ModerationSubmissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    PolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ApprovedMetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    RightsReferenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_publication_snapshots", x => x.Id);
                    table.UniqueConstraint("AK_publication_snapshots_AssetId_Id", x => new { x.AssetId, x.Id });
                    table.CheckConstraint("CK_publication_snapshots_sha", "length(\"ContentSha256\") = 64");
                    table.ForeignKey(
                        name: "FK_publication_snapshots_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_publication_snapshots_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_publication_snapshots_code_analysis_report_headers_AssetId_~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.CodeAnalysisReportHeaderId },
                        principalTable: "code_analysis_report_headers",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_publication_snapshots_moderation_submissions_AssetId_AssetV~",
                        columns: x => new { x.AssetId, x.AssetVersionId, x.ModerationSubmissionId },
                        principalTable: "moderation_submissions",
                        principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
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
                    LicenseTerms = table.Column<string>(type: "character varying(16000)", maxLength: 16000, nullable: false),
                    PublicationSnapshotId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_lines", x => x.Id);
                    table.CheckConstraint("CK_order_lines_list_price_positive", "\"ListPrice\" > 0");
                    table.CheckConstraint("CK_order_lines_position_positive", "\"Position\" > 0");
                    table.CheckConstraint("CK_order_lines_price_paid_positive", "\"PricePaid\" > 0");
                    table.CheckConstraint("CK_order_lines_version_positive", "\"VersionNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_order_lines_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
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
                        name: "FK_order_lines_publication_snapshots_PublicationSnapshotId",
                        column: x => x.PublicationSnapshotId,
                        principalTable: "publication_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_lines_users_SellerId",
                        column: x => x.SellerId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "purchases",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    PurchasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PublicationSnapshotId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_purchases", x => x.Id);
                    table.ForeignKey(
                        name: "FK_purchases_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchases_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchases_order_lines_OrderLineId",
                        column: x => x.OrderLineId,
                        principalTable: "order_lines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchases_publication_snapshots_PublicationSnapshotId",
                        column: x => x.PublicationSnapshotId,
                        principalTable: "publication_snapshots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_purchases_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_evidence_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    SecurityRecheckId = table.Column<Guid>(type: "uuid", nullable: true),
                    EvidenceKind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    JobId = table.Column<Guid>(type: "uuid", nullable: true),
                    Generation = table.Column<int>(type: "integer", nullable: false),
                    DefinitionVersion = table.Column<int>(type: "integer", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SecurityPolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ImplementationIdentity = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EngineIdentity = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    EngineVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    SignatureIdentity = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    SignatureObservedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SignatureValidUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    IdentityAvailable = table.Column<bool>(type: "boolean", nullable: false),
                    ScannedByteCount = table.Column<long>(type: "bigint", nullable: false),
                    ScannedEntryCount = table.Column<int>(type: "integer", nullable: true),
                    ScopeComplete = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EvidenceDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_evidence_records", x => x.Id);
                    table.CheckConstraint("CK_security_evidence_records_content_sha", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_security_evidence_records_digest", "\"EvidenceDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_security_evidence_records_generation", "\"Generation\" > 0");
                    table.CheckConstraint("CK_security_evidence_records_identity", "(\"EvidenceKind\" = 'ARCHIVE_VALIDATION' AND \"ImplementationIdentity\" IS NOT NULL AND \"EngineIdentity\" IS NULL) OR (\"EvidenceKind\" = 'MALWARE_SCAN' AND \"ImplementationIdentity\" IS NULL AND ((\"IdentityAvailable\" AND \"EngineIdentity\" IS NOT NULL) OR (NOT \"IdentityAvailable\" AND \"EngineIdentity\" IS NULL)))");
                    table.CheckConstraint("CK_security_evidence_records_kind", "\"EvidenceKind\" IN ('ARCHIVE_VALIDATION', 'MALWARE_SCAN')");
                    table.CheckConstraint("CK_security_evidence_records_outcome", "\"Outcome\" IN ('VALID', 'CLEAN', 'UNSAFE', 'INFECTED', 'ERROR')");
                    table.CheckConstraint("CK_security_evidence_records_scanned_bytes", "\"ScannedByteCount\" >= 0");
                    table.CheckConstraint("CK_security_evidence_records_signature_scope", "(\"IdentityAvailable\" AND \"SignatureObservedAt\" IS NOT NULL AND \"SignatureValidUntil\" IS NOT NULL) OR (NOT \"IdentityAvailable\" AND \"SignatureObservedAt\" IS NULL AND \"SignatureValidUntil\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_security_evidence_records_asset_processing_jobs_JobId",
                        column: x => x.JobId,
                        principalTable: "asset_processing_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_rechecks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssetVersionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentSha256 = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    SecurityGeneration = table.Column<int>(type: "integer", nullable: false),
                    SecurityPolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefinitionVersion = table.Column<int>(type: "integer", nullable: false),
                    OperationDigest = table.Column<string>(type: "char(64)", fixedLength: true, nullable: false),
                    OperationReceiptId = table.Column<Guid>(type: "uuid", nullable: false),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SelectedGeneration = table.Column<int>(type: "integer", nullable: true),
                    ParentRecheckId = table.Column<Guid>(type: "uuid", nullable: true),
                    ParentAnalysisRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    ArchiveEvidenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    MalwareEvidenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedGeneration = table.Column<int>(type: "integer", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorSummary = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    RequestedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_rechecks", x => x.Id);
                    table.UniqueConstraint("AK_security_rechecks_AssetId_AssetVersionId_Id", x => new { x.AssetId, x.AssetVersionId, x.Id });
                    table.CheckConstraint("CK_security_rechecks_content_sha", "\"ContentSha256\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_security_rechecks_definition_version", "\"DefinitionVersion\" > 0");
                    table.CheckConstraint("CK_security_rechecks_generation", "\"SecurityGeneration\" > 0");
                    table.CheckConstraint("CK_security_rechecks_operation_digest", "\"OperationDigest\" ~ '^[0-9a-f]{64}$'");
                    table.CheckConstraint("CK_security_rechecks_selected_generation", "\"SelectedGeneration\" IS NULL OR \"SelectedGeneration\" = \"SecurityGeneration\"");
                    table.CheckConstraint("CK_security_rechecks_state", "\"State\" IN ('QUEUED', 'INSPECTING', 'SCANNING', 'SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED')");
                    table.CheckConstraint("CK_security_rechecks_terminal_completed_at", "(\"State\" IN ('SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED') AND \"CompletedAt\" IS NOT NULL) OR (\"State\" NOT IN ('SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED') AND \"CompletedAt\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_security_rechecks_asset_versions_AssetId_AssetVersionId",
                        columns: x => new { x.AssetId, x.AssetVersionId },
                        principalTable: "asset_versions",
                        principalColumns: new[] { "AssetId", "Id" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_rechecks_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_rechecks_security_evidence_records_ArchiveEvidence~",
                        column: x => x.ArchiveEvidenceId,
                        principalTable: "security_evidence_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_rechecks_security_evidence_records_MalwareEvidence~",
                        column: x => x.MalwareEvidenceId,
                        principalTable: "security_evidence_records",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_security_rechecks_security_rechecks_ParentRecheckId",
                        column: x => x.ParentRecheckId,
                        principalTable: "security_rechecks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.InsertData(
                table: "social_platforms",
                columns: new[] { "Id", "CreatedAt", "IconName", "Name", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("a3b9c0d1-e2f7-0a8b-5c4d-3e2f1a0b9c8d"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "artstation", "ArtStation", null },
                    { new Guid("a7b3c4d5-e6f1-4a2b-9c8d-7e6f5a4b3c2d"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "twitter", "Twitter / X", null },
                    { new Guid("b4c0d1e2-f3a8-1b9c-6d5e-4f3a2b1c0d9e"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "globe", "Personal Website", null },
                    { new Guid("b8c4d5e6-f7a2-5b3c-0d9e-8f7a6b5c4d3e"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "github", "GitHub", null },
                    { new Guid("c9d5e6f7-a8b3-6c4d-1e0f-9a8b7c6d5e4f"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "linkedin", "LinkedIn", null },
                    { new Guid("d0e6f7a8-b9c4-7d5e-2f1a-0b9c8d7e6f5a"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "youtube", "YouTube", null },
                    { new Guid("e1f7a8b9-c0d5-8e6f-3a2b-1c0d9e8f7a6b"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "discord", "Discord", null },
                    { new Guid("f2a8b9c0-d1e6-9f7a-4b3c-2d1e0f9a8b7c"), new DateTimeOffset(new DateTime(2026, 2, 23, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "instagram", "Instagram", null }
                });

            migrationBuilder.CreateIndex(
                name: "IX_analysis_capacity_slots_hard_deadline",
                table: "analysis_capacity_slots",
                column: "HardDeadlineUtc");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_capacity_slots_ReservedJobId",
                table: "analysis_capacity_slots",
                column: "ReservedJobId");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_capacity_slots_ReservedRunId",
                table: "analysis_capacity_slots",
                column: "ReservedRunId");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_capacity_slots_service",
                table: "analysis_capacity_slots",
                column: "ServiceKey");

            migrationBuilder.CreateIndex(
                name: "IX_analysis_capacity_slots_service_state",
                table: "analysis_capacity_slots",
                columns: new[] { "ServiceKey", "State" });

            migrationBuilder.CreateIndex(
                name: "UIX_analysis_capacity_slots_service_slot",
                table: "analysis_capacity_slots",
                columns: new[] { "ServiceKey", "SlotNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_analysis_service_registrations_service_key",
                table: "analysis_service_registrations",
                column: "ServiceKey",
                unique: true);

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

            migrationBuilder.CreateIndex(
                name: "UIX_asset_draft_workspaces_asset_pre_upload",
                table: "asset_draft_workspaces",
                column: "AssetId",
                unique: true,
                filter: "\"AssetVersionId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_draft_workspaces_asset_version",
                table: "asset_draft_workspaces",
                columns: new[] { "AssetId", "AssetVersionId" },
                unique: true,
                filter: "\"AssetVersionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_asset_embeddings_model_key_asset_id",
                table: "asset_embeddings",
                columns: new[] { "ModelKey", "AssetId" });

            migrationBuilder.CreateIndex(
                name: "UIX_asset_embeddings_asset_id_model_key",
                table: "asset_embeddings",
                columns: new[] { "AssetId", "ModelKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_asset_material_metadata_revisions_AssetId_AssetVersionId",
                table: "asset_material_metadata_revisions",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_material_metadata_revisions_AssetId_WorkspaceId_Revis~",
                table: "asset_material_metadata_revisions",
                columns: new[] { "AssetId", "WorkspaceId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_asset_material_metadata_revisions_AssetId_WorkspaceId_Works~",
                table: "asset_material_metadata_revisions",
                columns: new[] { "AssetId", "WorkspaceId", "WorkspaceVersionScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_material_metadata_revisions_WorkspaceId_Revision",
                table: "asset_material_metadata_revisions",
                columns: new[] { "WorkspaceId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_asset_processing_jobs_AssetId_AssetVersionId",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_processing_jobs_AssetId_AssetVersionId_AnalysisRunId",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId", "AnalysisRunId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_processing_jobs_AssetId_AssetVersionId_SecurityRechec~",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId", "SecurityRecheckId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_processing_jobs_claim",
                table: "asset_processing_jobs",
                columns: new[] { "Status", "AvailableAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_processing_jobs_lease_expiry",
                table: "asset_processing_jobs",
                column: "LeaseExpiresAt");

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
                filter: "\"Type\" NOT IN ('EMBEDDING_GENERATION', 'CODE_ANALYSIS') AND \"SecurityRecheckId\" IS NULL AND \"AnalysisRunId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_processing_jobs_recheck_idempotency",
                table: "asset_processing_jobs",
                columns: new[] { "SecurityRecheckId", "Type", "DefinitionVersion" },
                unique: true,
                filter: "\"SecurityRecheckId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_asset_processing_jobs_run_idempotency",
                table: "asset_processing_jobs",
                columns: new[] { "AnalysisRunId", "Type", "DefinitionVersion" },
                unique: true,
                filter: "\"AnalysisRunId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_asset_seller_evidence_revisions_AssetId_AssetVersionId",
                table: "asset_seller_evidence_revisions",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_seller_evidence_revisions_AssetId_WorkspaceId_Workspa~",
                table: "asset_seller_evidence_revisions",
                columns: new[] { "AssetId", "WorkspaceId", "WorkspaceVersionScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_seller_evidence_revisions_WorkspaceId_Revision",
                table: "asset_seller_evidence_revisions",
                columns: new[] { "WorkspaceId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_asset_source_declaration_revisions_AssetId_AssetVersionId",
                table: "asset_source_declaration_revisions",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_source_declaration_revisions_AssetId_WorkspaceId_Work~",
                table: "asset_source_declaration_revisions",
                columns: new[] { "AssetId", "WorkspaceId", "WorkspaceVersionScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_source_declaration_revisions_WorkspaceId_Revision",
                table: "asset_source_declaration_revisions",
                columns: new[] { "WorkspaceId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_asset_tags_TagId_AssetId",
                table: "asset_tags",
                columns: new[] { "TagId", "AssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_versions_processing_status",
                table: "asset_versions",
                column: "ProcessingStatus");

            migrationBuilder.CreateIndex(
                name: "IX_asset_versions_SelectedCodeAnalysisReportId",
                table: "asset_versions",
                column: "SelectedCodeAnalysisReportId");

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
                name: "IX_assets_author_id",
                table: "assets",
                columns: new[] { "AuthorId", "Id" });

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
                name: "IX_assets_Id_CurrentPublicationSnapshotId",
                table: "assets",
                columns: new[] { "Id", "CurrentPublicationSnapshotId" });

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
                name: "IX_audit_logs_Action_OccurredAt_Id",
                table: "audit_logs",
                columns: new[] { "Action", "OccurredAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_ActorUserId_OccurredAt_Id",
                table: "audit_logs",
                columns: new[] { "ActorUserId", "OccurredAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_OccurredAt_Id",
                table: "audit_logs",
                columns: new[] { "OccurredAt", "Id" },
                descending: new bool[0]);

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_Outcome_OccurredAt_Id",
                table: "audit_logs",
                columns: new[] { "Outcome", "OccurredAt", "Id" },
                descending: new[] { false, true, true });

            migrationBuilder.CreateIndex(
                name: "IX_audit_logs_ResourceType_ResourceId_OccurredAt_Id",
                table: "audit_logs",
                columns: new[] { "ResourceType", "ResourceId", "OccurredAt", "Id" },
                descending: new[] { false, false, true, true });

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
                name: "IX_categories_Slug",
                table: "categories",
                column: "Slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_categories_Slug_trgm",
                table: "categories",
                column: "Slug")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_asset",
                table: "checkout_intent_items",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_AssetId_AssetVersionId",
                table: "checkout_intent_items",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_PublicationSnapshotId",
                table: "checkout_intent_items",
                column: "PublicationSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intent_items_seller_intent",
                table: "checkout_intent_items",
                columns: new[] { "SellerId", "CheckoutIntentId" });

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
                name: "IX_checkout_intents_pending_attached_reconcile",
                table: "checkout_intents",
                columns: new[] { "Status", "CreatedAt", "Id" },
                filter: "\"Status\" = 'PENDING' AND \"StripeSessionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_checkout_intents_status_expires",
                table: "checkout_intents",
                columns: new[] { "Status", "ExpiresAt", "Id" });

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
                filter: "\"Status\" = 'PENDING' AND \"AssetId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_checkout_intents_user_bundle_pending",
                table: "checkout_intents",
                columns: new[] { "UserId", "BundleId" },
                unique: true,
                filter: "\"Status\" = 'PENDING' AND \"BundleId\" IS NOT NULL");

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
                name: "UIX_code_analysis_policy_versions_identity",
                table: "code_analysis_policy_versions",
                columns: new[] { "PolicyKey", "PolicyDigest" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_report_admissions_report",
                table: "code_analysis_report_admissions",
                column: "ReportId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_report_findings_report_key",
                table: "code_analysis_report_findings",
                columns: new[] { "ReportId", "FindingKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_report_headers_version",
                table: "code_analysis_report_headers",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_report_headers_finalized_run",
                table: "code_analysis_report_headers",
                column: "AnalysisRunId",
                unique: true,
                filter: "\"AnalysisRunId\" IS NOT NULL AND \"IsFinalized\"");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_report_headers_python_report_id",
                table: "code_analysis_report_headers",
                column: "PythonReportId",
                unique: true,
                filter: "\"PythonReportId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_report_stages_ReuseParentRunId",
                table: "code_analysis_report_stages",
                column: "ReuseParentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_report_stages_ReuseParentStageId",
                table: "code_analysis_report_stages",
                column: "ReuseParentStageId");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_report_stages_report_stage",
                table: "code_analysis_report_stages",
                columns: new[] { "ReportId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_reuse_artifacts_digest",
                table: "code_analysis_reuse_artifacts",
                columns: new[] { "Stage", "ArtifactDigest" });

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_reuse_artifacts_SourceReportId",
                table: "code_analysis_reuse_artifacts",
                column: "SourceReportId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_reuse_artifacts_SourceStageId",
                table: "code_analysis_reuse_artifacts",
                column: "SourceStageId");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_reuse_artifacts_run_stage",
                table: "code_analysis_reuse_artifacts",
                columns: new[] { "SourceRunId", "Stage" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_ArchiveEvidenceId",
                table: "code_analysis_runs",
                column: "ArchiveEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_AssetDraftWorkspaceId",
                table: "code_analysis_runs",
                column: "AssetDraftWorkspaceId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_DeclarationRevisionId",
                table: "code_analysis_runs",
                column: "DeclarationRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_MalwareEvidenceId",
                table: "code_analysis_runs",
                column: "MalwareEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_MaterialRevisionId",
                table: "code_analysis_runs",
                column: "MaterialRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_ParentRunId",
                table: "code_analysis_runs",
                column: "ParentRunId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_PolicyVersionId",
                table: "code_analysis_runs",
                column: "PolicyVersionId");

            migrationBuilder.CreateIndex(
                name: "IX_code_analysis_runs_SecurityRecheckId",
                table: "code_analysis_runs",
                column: "SecurityRecheckId");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_runs_active_generation",
                table: "code_analysis_runs",
                columns: new[] { "AssetVersionId", "SelectionGeneration" },
                unique: true,
                filter: "\"State\" IN ('QUEUED', 'RUNNING')");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_runs_active_input",
                table: "code_analysis_runs",
                columns: new[] { "AssetId", "AssetVersionId", "ContentSha256", "PolicyDigest", "DeclarationDigest", "MaterialDigest", "SelectionGeneration", "SecurityGeneration", "ArchiveEvidenceId", "MalwareEvidenceId" },
                unique: true,
                filter: "\"State\" IN ('QUEUED', 'RUNNING')");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_runs_input_digest",
                table: "code_analysis_runs",
                column: "InputDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_runs_job",
                table: "code_analysis_runs",
                column: "JobId",
                unique: true,
                filter: "\"JobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_code_analysis_runs_scope",
                table: "code_analysis_runs",
                columns: new[] { "AssetId", "AssetVersionId", "Id" },
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
                name: "IX_collections_Description_trgm",
                table: "collections",
                column: "Description")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_collections_public_status_published",
                table: "collections",
                columns: new[] { "Status", "PublishedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_collections_seller_status_created",
                table: "collections",
                columns: new[] { "SellerId", "Status", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_collections_Title_trgm",
                table: "collections",
                column: "Title")
                .Annotation("Npgsql:IndexMethod", "GIN")
                .Annotation("Npgsql:IndexOperators", new[] { "gin_trgm_ops" });

            migrationBuilder.CreateIndex(
                name: "IX_coverage_assessments_ModeratorUserId",
                table: "coverage_assessments",
                column: "ModeratorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_coverage_assessments_RequestId",
                table: "coverage_assessments",
                column: "RequestId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_coverage_review_requests_AssetId_AssetVersionId_ReportId",
                table: "coverage_review_requests",
                columns: new[] { "AssetId", "AssetVersionId", "ReportId" });

            migrationBuilder.CreateIndex(
                name: "IX_coverage_review_requests_AssetId_WorkspaceId_AssetVersionId",
                table: "coverage_review_requests",
                columns: new[] { "AssetId", "WorkspaceId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_coverage_review_requests_AssetVersionId_RequestedWorkspaceR~",
                table: "coverage_review_requests",
                columns: new[] { "AssetVersionId", "RequestedWorkspaceRevision", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_coverage_review_requests_OwnerUserId",
                table: "coverage_review_requests",
                column: "OwnerUserId");

            migrationBuilder.CreateIndex(
                name: "IX_coverage_review_requests_TupleDigest",
                table: "coverage_review_requests",
                column: "TupleDigest",
                unique: true,
                filter: "\"IsOpen\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_email_actions_ExpiresAt",
                table: "email_actions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_email_actions_UserId_Purpose",
                table: "email_actions",
                columns: new[] { "UserId", "Purpose" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_json_mutation_idempotency_ActorUserId_OperationKind_OperationId",
                table: "json_mutation_idempotency",
                columns: new[] { "ActorUserId", "OperationKind", "OperationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_json_mutation_idempotency_ActorUserId_OperationKind_Request~",
                table: "json_mutation_idempotency",
                columns: new[] { "ActorUserId", "OperationKind", "RequestDigest" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_decisions_PublicationSnapshotId",
                table: "moderation_decisions",
                column: "PublicationSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_decisions_SubmissionId",
                table: "moderation_decisions",
                column: "SubmissionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_moderation_finding_dispositions_AssetId_AssetVersionId_Repo~",
                table: "moderation_finding_dispositions",
                columns: new[] { "AssetId", "AssetVersionId", "ReportId" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_finding_dispositions_AssetId_AssetVersionId_Subm~",
                table: "moderation_finding_dispositions",
                columns: new[] { "AssetId", "AssetVersionId", "SubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_finding_dispositions_ModeratorUserId",
                table: "moderation_finding_dispositions",
                column: "ModeratorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_finding_dispositions_ReportId_FindingId",
                table: "moderation_finding_dispositions",
                columns: new[] { "ReportId", "FindingId" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_finding_dispositions_SubmissionId_FindingId_Case~",
                table: "moderation_finding_dispositions",
                columns: new[] { "SubmissionId", "FindingId", "CaseRevision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submission_history_SubmissionId_CreatedAt_Id",
                table: "moderation_submission_history",
                columns: new[] { "SubmissionId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_AssetId_AssetVersionId_CodeAnalysisR~",
                table: "moderation_submissions",
                columns: new[] { "AssetId", "AssetVersionId", "CodeAnalysisReportHeaderId" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_AssetId_AssetVersionId_PreviousSubmi~",
                table: "moderation_submissions",
                columns: new[] { "AssetId", "AssetVersionId", "PreviousSubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_AssetId_WorkspaceId_WorkspaceVersion~",
                table: "moderation_submissions",
                columns: new[] { "AssetId", "WorkspaceId", "WorkspaceVersionScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_ClaimHolderId",
                table: "moderation_submissions",
                column: "ClaimHolderId");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_CoverageAssessmentId",
                table: "moderation_submissions",
                column: "CoverageAssessmentId");

            migrationBuilder.CreateIndex(
                name: "IX_moderation_submissions_queue",
                table: "moderation_submissions",
                columns: new[] { "CreatedAt", "Id" },
                filter: "\"State\" IN ('SUBMITTED', 'IN_REVIEW')");

            migrationBuilder.CreateIndex(
                name: "UIX_moderation_submissions_active_case",
                table: "moderation_submissions",
                column: "AssetVersionId",
                unique: true,
                filter: "\"State\" IN ('SUBMITTED', 'IN_REVIEW')");

            migrationBuilder.CreateIndex(
                name: "UIX_moderation_submissions_appeal_decision",
                table: "moderation_submissions",
                column: "AppealOfDecisionId",
                unique: true,
                filter: "\"AppealOfDecisionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_AssetId_AssetVersionId",
                table: "order_lines",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_PublicationSnapshotId",
                table: "order_lines",
                column: "PublicationSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_seller_asset_order",
                table: "order_lines",
                columns: new[] { "SellerId", "AssetId", "OrderId" });

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
                name: "IX_orders_bundle_purchased_id",
                table: "orders",
                columns: new[] { "BundleId", "PurchasedAt", "Id" },
                filter: "\"BundleId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_orders_BundleRevisionId",
                table: "orders",
                column: "BundleRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_purchased_id",
                table: "orders",
                columns: new[] { "PurchasedAt", "Id" });

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

            migrationBuilder.CreateIndex(
                name: "IX_outbox_email_deliveries_MessageId",
                table: "outbox_email_deliveries",
                column: "MessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_email_deliveries_OutboxMessageId",
                table: "outbox_email_deliveries",
                column: "OutboxMessageId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_dead_letter",
                table: "outbox_messages",
                columns: new[] { "Status", "DeadLetteredAt", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_outbox_messages_dispatch",
                table: "outbox_messages",
                columns: new[] { "Status", "NextAttemptAt", "LockedUntil", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_paid_checkout_reconciliation_holds_CheckoutIntentId",
                table: "paid_checkout_reconciliation_holds",
                column: "CheckoutIntentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_paid_checkout_reconciliation_holds_StripeEventId",
                table: "paid_checkout_reconciliation_holds",
                column: "StripeEventId",
                unique: true,
                filter: "\"StripeEventId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_processed_stripe_webhook_events_StripeEventId",
                table: "processed_stripe_webhook_events",
                column: "StripeEventId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_product_analytics_daily_type_product_day",
                table: "product_analytics_daily",
                columns: new[] { "ProductType", "ProductId", "DayUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_publication_snapshots_AssetId_AssetVersionId_CodeAnalysisRe~",
                table: "publication_snapshots",
                columns: new[] { "AssetId", "AssetVersionId", "CodeAnalysisReportHeaderId" });

            migrationBuilder.CreateIndex(
                name: "IX_publication_snapshots_AssetId_AssetVersionId_ModerationSubm~",
                table: "publication_snapshots",
                columns: new[] { "AssetId", "AssetVersionId", "ModerationSubmissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_publication_snapshots_ModerationSubmissionId",
                table: "publication_snapshots",
                column: "ModerationSubmissionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_purchases_AssetId_AssetVersionId",
                table: "purchases",
                columns: new[] { "AssetId", "AssetVersionId" });

            migrationBuilder.CreateIndex(
                name: "IX_purchases_PublicationSnapshotId",
                table: "purchases",
                column: "PublicationSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_purchases_user_purchased_at_id",
                table: "purchases",
                columns: new[] { "UserId", "PurchasedAt", "Id" },
                descending: new[] { false, true, false });

            migrationBuilder.CreateIndex(
                name: "UIX_purchases_order_line",
                table: "purchases",
                column: "OrderLineId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_purchases_user_asset",
                table: "purchases",
                columns: new[] { "UserId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_daily_source_target_day",
                table: "recommendation_daily",
                columns: new[] { "SourceAssetId", "TargetAssetId", "DayUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_events_ActorUserId_EventType_OccurredAt",
                table: "recommendation_events",
                columns: new[] { "ActorUserId", "EventType", "OccurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_events_OccurredAt_brin",
                table: "recommendation_events",
                column: "OccurredAt")
                .Annotation("Npgsql:IndexMethod", "brin");

            migrationBuilder.CreateIndex(
                name: "IX_recommendation_events_OccurredAt_Id",
                table: "recommendation_events",
                columns: new[] { "OccurredAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "UX_recommendation_events_ExposureId_EventType_TargetAssetId",
                table: "recommendation_events",
                columns: new[] { "ExposureId", "EventType", "TargetAssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_expires_at",
                table: "refresh_tokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId",
                table: "refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_reviews_asset_created_id",
                table: "reviews",
                columns: new[] { "AssetId", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_reviews_AssetId",
                table: "reviews",
                column: "AssetId");

            migrationBuilder.CreateIndex(
                name: "IX_reviews_UserId_AssetId",
                table: "reviews",
                columns: new[] { "UserId", "AssetId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_evidence_records_JobId",
                table: "security_evidence_records",
                column: "JobId");

            migrationBuilder.CreateIndex(
                name: "UIX_security_evidence_records_digest",
                table: "security_evidence_records",
                column: "EvidenceDigest",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_security_evidence_records_recheck_kind",
                table: "security_evidence_records",
                columns: new[] { "SecurityRecheckId", "EvidenceKind" },
                unique: true,
                filter: "\"SecurityRecheckId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UIX_security_evidence_records_upload_kind",
                table: "security_evidence_records",
                columns: new[] { "AssetVersionId", "EvidenceKind" },
                unique: true,
                filter: "\"SecurityRecheckId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_security_rechecks_ArchiveEvidenceId",
                table: "security_rechecks",
                column: "ArchiveEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_security_rechecks_MalwareEvidenceId",
                table: "security_rechecks",
                column: "MalwareEvidenceId");

            migrationBuilder.CreateIndex(
                name: "IX_security_rechecks_ParentRecheckId",
                table: "security_rechecks",
                column: "ParentRecheckId");

            migrationBuilder.CreateIndex(
                name: "UIX_security_rechecks_generation",
                table: "security_rechecks",
                columns: new[] { "AssetVersionId", "SecurityGeneration" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_security_rechecks_operation_receipt",
                table: "security_rechecks",
                columns: new[] { "AssetVersionId", "OperationReceiptId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_security_rechecks_scope",
                table: "security_rechecks",
                columns: new[] { "AssetId", "AssetVersionId", "Id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UIX_security_rechecks_selected",
                table: "security_rechecks",
                column: "AssetVersionId",
                unique: true,
                filter: "\"SelectedGeneration\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_ActorUserId",
                table: "seller_responses",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_AssetId_AssetVersionId_ReportId",
                table: "seller_responses",
                columns: new[] { "AssetId", "AssetVersionId", "ReportId" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_AssetId_WorkspaceId_WorkspaceVersionScopeK~",
                table: "seller_responses",
                columns: new[] { "AssetId", "WorkspaceId", "WorkspaceVersionScopeKey" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_AssetVersionId_SellerEvidenceRevision_Id",
                table: "seller_responses",
                columns: new[] { "AssetVersionId", "SellerEvidenceRevision", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_ReportId_FindingId",
                table: "seller_responses",
                columns: new[] { "ReportId", "FindingId" });

            migrationBuilder.CreateIndex(
                name: "IX_seller_responses_WorkspaceId_SellerEvidenceRevision_Finding~",
                table: "seller_responses",
                columns: new[] { "WorkspaceId", "SellerEvidenceRevision", "FindingId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_social_platforms_Name",
                table: "social_platforms",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_tags_Name",
                table: "tags",
                column: "Name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_recipient_unread_created_id",
                table: "user_notifications",
                columns: new[] { "RecipientUserId", "CreatedAt", "Id" },
                descending: new[] { false, true, false },
                filter: "\"ReadAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_RecipientUserId_CreatedAt",
                table: "user_notifications",
                columns: new[] { "RecipientUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_user_notifications_SourceOutboxMessageId",
                table: "user_notifications",
                column: "SourceOutboxMessageId",
                unique: true,
                filter: "\"SourceOutboxMessageId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_user_social_links_PlatformId",
                table: "user_social_links",
                column: "PlatformId");

            migrationBuilder.CreateIndex(
                name: "IX_user_social_links_UserId",
                table: "user_social_links",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_users_Email",
                table: "users",
                column: "Email",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_Username",
                table: "users",
                column: "Username",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_analysis_capacity_slots_asset_processing_jobs_ReservedJobId",
                table: "analysis_capacity_slots",
                column: "ReservedJobId",
                principalTable: "asset_processing_jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_analysis_capacity_slots_code_analysis_runs_ReservedRunId",
                table: "analysis_capacity_slots",
                column: "ReservedRunId",
                principalTable: "code_analysis_runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_archive_analyses_asset_versions_AssetVersionId",
                table: "asset_archive_analyses",
                column: "AssetVersionId",
                principalTable: "asset_versions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_draft_workspaces_asset_versions_AssetId_AssetVersionId",
                table: "asset_draft_workspaces",
                columns: new[] { "AssetId", "AssetVersionId" },
                principalTable: "asset_versions",
                principalColumns: new[] { "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_draft_workspaces_assets_AssetId",
                table: "asset_draft_workspaces",
                column: "AssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_embeddings_assets_AssetId",
                table: "asset_embeddings",
                column: "AssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_listing_suggestions_asset_processing_jobs_JobId",
                table: "asset_listing_suggestions",
                column: "JobId",
                principalTable: "asset_processing_jobs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_material_metadata_revisions_asset_versions_AssetId_As~",
                table: "asset_material_metadata_revisions",
                columns: new[] { "AssetId", "AssetVersionId" },
                principalTable: "asset_versions",
                principalColumns: new[] { "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_material_metadata_revisions_assets_AssetId",
                table: "asset_material_metadata_revisions",
                column: "AssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_processing_jobs_asset_versions_AssetId_AssetVersionId",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId" },
                principalTable: "asset_versions",
                principalColumns: new[] { "AssetId", "Id" },
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_processing_jobs_assets_AssetId",
                table: "asset_processing_jobs",
                column: "AssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_processing_jobs_code_analysis_runs_AssetId_AssetVersi~",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId", "AnalysisRunId" },
                principalTable: "code_analysis_runs",
                principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_processing_jobs_security_rechecks_AssetId_AssetVersio~",
                table: "asset_processing_jobs",
                columns: new[] { "AssetId", "AssetVersionId", "SecurityRecheckId" },
                principalTable: "security_rechecks",
                principalColumns: new[] { "AssetId", "AssetVersionId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_seller_evidence_revisions_asset_versions_AssetId_Asse~",
                table: "asset_seller_evidence_revisions",
                columns: new[] { "AssetId", "AssetVersionId" },
                principalTable: "asset_versions",
                principalColumns: new[] { "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_seller_evidence_revisions_assets_AssetId",
                table: "asset_seller_evidence_revisions",
                column: "AssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_source_declaration_revisions_asset_versions_AssetId_A~",
                table: "asset_source_declaration_revisions",
                columns: new[] { "AssetId", "AssetVersionId" },
                principalTable: "asset_versions",
                principalColumns: new[] { "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_source_declaration_revisions_assets_AssetId",
                table: "asset_source_declaration_revisions",
                column: "AssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_tags_assets_AssetId",
                table: "asset_tags",
                column: "AssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_versions_assets_AssetId",
                table: "asset_versions",
                column: "AssetId",
                principalTable: "assets",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_asset_versions_code_analysis_report_headers_SelectedCodeAna~",
                table: "asset_versions",
                column: "SelectedCodeAnalysisReportId",
                principalTable: "code_analysis_report_headers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_assets_publication_snapshots_Id_CurrentPublicationSnapshotId",
                table: "assets",
                columns: new[] { "Id", "CurrentPublicationSnapshotId" },
                principalTable: "publication_snapshots",
                principalColumns: new[] { "AssetId", "Id" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_checkout_intent_items_publication_snapshots_PublicationSnap~",
                table: "checkout_intent_items",
                column: "PublicationSnapshotId",
                principalTable: "publication_snapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_report_admissions_code_analysis_report_header~",
                table: "code_analysis_report_admissions",
                column: "ReportId",
                principalTable: "code_analysis_report_headers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_report_findings_code_analysis_report_headers_~",
                table: "code_analysis_report_findings",
                column: "ReportId",
                principalTable: "code_analysis_report_headers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_report_headers_code_analysis_runs_AnalysisRun~",
                table: "code_analysis_report_headers",
                column: "AnalysisRunId",
                principalTable: "code_analysis_runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_report_stages_code_analysis_runs_ReuseParentR~",
                table: "code_analysis_report_stages",
                column: "ReuseParentRunId",
                principalTable: "code_analysis_runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_reuse_artifacts_code_analysis_runs_SourceRunId",
                table: "code_analysis_reuse_artifacts",
                column: "SourceRunId",
                principalTable: "code_analysis_runs",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_runs_security_evidence_records_ArchiveEvidenc~",
                table: "code_analysis_runs",
                column: "ArchiveEvidenceId",
                principalTable: "security_evidence_records",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_runs_security_evidence_records_MalwareEvidenc~",
                table: "code_analysis_runs",
                column: "MalwareEvidenceId",
                principalTable: "security_evidence_records",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_code_analysis_runs_security_rechecks_SecurityRecheckId",
                table: "code_analysis_runs",
                column: "SecurityRecheckId",
                principalTable: "security_rechecks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_moderation_decisions_moderation_submissions_SubmissionId",
                table: "moderation_decisions",
                column: "SubmissionId",
                principalTable: "moderation_submissions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_moderation_decisions_publication_snapshots_PublicationSnaps~",
                table: "moderation_decisions",
                column: "PublicationSnapshotId",
                principalTable: "publication_snapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_security_evidence_records_security_rechecks_SecurityRecheck~",
                table: "security_evidence_records",
                column: "SecurityRecheckId",
                principalTable: "security_rechecks",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_code_analysis_runs_asset_processing_jobs_JobId",
                table: "code_analysis_runs");

            migrationBuilder.DropForeignKey(
                name: "FK_security_evidence_records_asset_processing_jobs_JobId",
                table: "security_evidence_records");

            migrationBuilder.DropForeignKey(
                name: "FK_code_analysis_report_headers_code_analysis_runs_AnalysisRun~",
                table: "code_analysis_report_headers");

            migrationBuilder.DropForeignKey(
                name: "FK_asset_draft_workspaces_asset_versions_AssetId_AssetVersionId",
                table: "asset_draft_workspaces");

            migrationBuilder.DropForeignKey(
                name: "FK_code_analysis_report_headers_asset_versions_AssetId_AssetVe~",
                table: "code_analysis_report_headers");

            migrationBuilder.DropForeignKey(
                name: "FK_coverage_review_requests_asset_versions_AssetId_AssetVersio~",
                table: "coverage_review_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_moderation_submissions_asset_versions_AssetId_AssetVersionId",
                table: "moderation_submissions");

            migrationBuilder.DropForeignKey(
                name: "FK_publication_snapshots_asset_versions_AssetId_AssetVersionId",
                table: "publication_snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_security_rechecks_asset_versions_AssetId_AssetVersionId",
                table: "security_rechecks");

            migrationBuilder.DropForeignKey(
                name: "FK_asset_draft_workspaces_assets_AssetId",
                table: "asset_draft_workspaces");

            migrationBuilder.DropForeignKey(
                name: "FK_code_analysis_report_headers_assets_AssetId",
                table: "code_analysis_report_headers");

            migrationBuilder.DropForeignKey(
                name: "FK_moderation_submissions_assets_AssetId",
                table: "moderation_submissions");

            migrationBuilder.DropForeignKey(
                name: "FK_publication_snapshots_assets_AssetId",
                table: "publication_snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_security_rechecks_assets_AssetId",
                table: "security_rechecks");

            migrationBuilder.DropForeignKey(
                name: "FK_coverage_review_requests_asset_draft_workspaces_AssetId_Wor~",
                table: "coverage_review_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_moderation_submissions_asset_draft_workspaces_AssetId_Works~",
                table: "moderation_submissions");

            migrationBuilder.DropForeignKey(
                name: "FK_security_evidence_records_security_rechecks_SecurityRecheck~",
                table: "security_evidence_records");

            migrationBuilder.DropForeignKey(
                name: "FK_coverage_review_requests_code_analysis_report_headers_Asset~",
                table: "coverage_review_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_moderation_submissions_code_analysis_report_headers_AssetId~",
                table: "moderation_submissions");

            migrationBuilder.DropForeignKey(
                name: "FK_publication_snapshots_code_analysis_report_headers_AssetId_~",
                table: "publication_snapshots");

            migrationBuilder.DropForeignKey(
                name: "FK_moderation_decisions_publication_snapshots_PublicationSnaps~",
                table: "moderation_decisions");

            migrationBuilder.DropForeignKey(
                name: "FK_coverage_assessments_users_ModeratorUserId",
                table: "coverage_assessments");

            migrationBuilder.DropForeignKey(
                name: "FK_coverage_review_requests_users_OwnerUserId",
                table: "coverage_review_requests");

            migrationBuilder.DropForeignKey(
                name: "FK_moderation_submissions_users_ClaimHolderId",
                table: "moderation_submissions");

            migrationBuilder.DropForeignKey(
                name: "FK_coverage_assessments_coverage_review_requests_RequestId",
                table: "coverage_assessments");

            migrationBuilder.DropForeignKey(
                name: "FK_moderation_decisions_moderation_submissions_SubmissionId",
                table: "moderation_decisions");

            migrationBuilder.DropTable(
                name: "analysis_capacity_slots");

            migrationBuilder.DropTable(
                name: "analysis_service_registrations");

            migrationBuilder.DropTable(
                name: "analytics_events");

            migrationBuilder.DropTable(
                name: "asset_archive_analyses");

            migrationBuilder.DropTable(
                name: "asset_embeddings");

            migrationBuilder.DropTable(
                name: "asset_listing_suggestions");

            migrationBuilder.DropTable(
                name: "asset_tags");

            migrationBuilder.DropTable(
                name: "audit_logs");

            migrationBuilder.DropTable(
                name: "bundle_revision_items");

            migrationBuilder.DropTable(
                name: "checkout_intent_items");

            migrationBuilder.DropTable(
                name: "checkout_reservations");

            migrationBuilder.DropTable(
                name: "code_analysis_report_admissions");

            migrationBuilder.DropTable(
                name: "code_analysis_reuse_artifacts");

            migrationBuilder.DropTable(
                name: "collection_analytics_daily");

            migrationBuilder.DropTable(
                name: "collection_items");

            migrationBuilder.DropTable(
                name: "email_actions");

            migrationBuilder.DropTable(
                name: "json_mutation_idempotency");

            migrationBuilder.DropTable(
                name: "moderation_finding_dispositions");

            migrationBuilder.DropTable(
                name: "moderation_submission_history");

            migrationBuilder.DropTable(
                name: "outbox_email_deliveries");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "paid_checkout_reconciliation_holds");

            migrationBuilder.DropTable(
                name: "processed_stripe_webhook_events");

            migrationBuilder.DropTable(
                name: "product_analytics_daily");

            migrationBuilder.DropTable(
                name: "purchases");

            migrationBuilder.DropTable(
                name: "recommendation_daily");

            migrationBuilder.DropTable(
                name: "recommendation_events");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "reviews");

            migrationBuilder.DropTable(
                name: "seller_analytics_daily");

            migrationBuilder.DropTable(
                name: "seller_responses");

            migrationBuilder.DropTable(
                name: "storage_orphan_cleanup_states");

            migrationBuilder.DropTable(
                name: "traffic_analytics_daily");

            migrationBuilder.DropTable(
                name: "user_notifications");

            migrationBuilder.DropTable(
                name: "user_recommendation_preferences");

            migrationBuilder.DropTable(
                name: "user_social_links");

            migrationBuilder.DropTable(
                name: "user_source_click_affinity");

            migrationBuilder.DropTable(
                name: "user_tag_affinity");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "code_analysis_report_stages");

            migrationBuilder.DropTable(
                name: "collections");

            migrationBuilder.DropTable(
                name: "order_lines");

            migrationBuilder.DropTable(
                name: "asset_seller_evidence_revisions");

            migrationBuilder.DropTable(
                name: "code_analysis_report_findings");

            migrationBuilder.DropTable(
                name: "social_platforms");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "checkout_intents");

            migrationBuilder.DropTable(
                name: "bundle_revisions");

            migrationBuilder.DropTable(
                name: "bundles");

            migrationBuilder.DropTable(
                name: "asset_processing_jobs");

            migrationBuilder.DropTable(
                name: "code_analysis_runs");

            migrationBuilder.DropTable(
                name: "asset_material_metadata_revisions");

            migrationBuilder.DropTable(
                name: "asset_source_declaration_revisions");

            migrationBuilder.DropTable(
                name: "code_analysis_policy_versions");

            migrationBuilder.DropTable(
                name: "asset_versions");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "asset_draft_workspaces");

            migrationBuilder.DropTable(
                name: "security_rechecks");

            migrationBuilder.DropTable(
                name: "security_evidence_records");

            migrationBuilder.DropTable(
                name: "code_analysis_report_headers");

            migrationBuilder.DropTable(
                name: "publication_snapshots");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "coverage_review_requests");

            migrationBuilder.DropTable(
                name: "moderation_submissions");

            migrationBuilder.DropTable(
                name: "coverage_assessments");

            migrationBuilder.DropTable(
                name: "moderation_decisions");
        }
    }
}
