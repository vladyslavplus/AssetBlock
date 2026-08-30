using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using NpgsqlTypes;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace AssetBlock.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:pg_trgm", ",,");

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
                    search_vector = table.Column<NpgsqlTsVector>(type: "tsvector", nullable: true, computedColumnSql: "to_tsvector('simple'::regconfig, coalesce(\"Title\", '') || ' ' || coalesce(\"Description\", ''))", stored: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_assets", x => x.Id);
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
                        name: "FK_asset_tags_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
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
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_asset_versions", x => x.Id);
                    table.UniqueConstraint("AK_asset_versions_AssetId_Id", x => new { x.AssetId, x.Id });
                    table.CheckConstraint("CK_asset_versions_content_length_positive", "\"ContentLength\" > 0");
                    table.CheckConstraint("CK_asset_versions_processing_error_code", "\"ProcessingErrorCode\" IS NULL OR \"ProcessingErrorCode\" ~ '^[A-Z0-9_]{1,64}$'");
                    table.CheckConstraint("CK_asset_versions_processing_status", "\"ProcessingStatus\" IN ('PENDING_INSPECTION', 'PENDING_MALWARE_SCAN', 'READY', 'REJECTED', 'PROCESSING_FAILED')");
                    table.CheckConstraint("CK_asset_versions_ready_current", "\"IsCurrent\" = false OR (\"IsCurrent\" = true AND \"ProcessingStatus\" = 'READY')");
                    table.CheckConstraint("CK_asset_versions_state_error_consistency", "(\"ProcessingStatus\" IN ('PENDING_INSPECTION', 'PENDING_MALWARE_SCAN', 'READY') AND \"ProcessingErrorCode\" IS NULL AND \"ProcessingErrorSummary\" IS NULL) OR (\"ProcessingStatus\" IN ('REJECTED', 'PROCESSING_FAILED') AND \"ProcessingErrorCode\" IS NOT NULL AND \"ProcessingErrorSummary\" IS NOT NULL AND length(trim(\"ProcessingErrorSummary\")) > 0)");
                    table.CheckConstraint("CK_asset_versions_version_number_positive", "\"VersionNumber\" > 0");
                    table.ForeignKey(
                        name: "FK_asset_versions_assets_AssetId",
                        column: x => x.AssetId,
                        principalTable: "assets",
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
                        name: "FK_purchases_users_UserId",
                        column: x => x.UserId,
                        principalTable: "users",
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

            migrationBuilder.CreateIndex(
                name: "IX_asset_tags_TagId_AssetId",
                table: "asset_tags",
                columns: new[] { "TagId", "AssetId" });

            migrationBuilder.CreateIndex(
                name: "IX_asset_versions_processing_status",
                table: "asset_versions",
                column: "ProcessingStatus");

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
                name: "IX_email_actions_ExpiresAt",
                table: "email_actions",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_email_actions_UserId_Purpose",
                table: "email_actions",
                columns: new[] { "UserId", "Purpose" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_lines_AssetId_AssetVersionId",
                table: "order_lines",
                columns: new[] { "AssetId", "AssetVersionId" });

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
                name: "IX_purchases_AssetId_AssetVersionId",
                table: "purchases",
                columns: new[] { "AssetId", "AssetVersionId" });

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
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "analytics_events");

            migrationBuilder.DropTable(
                name: "asset_archive_analyses");

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
                name: "collection_analytics_daily");

            migrationBuilder.DropTable(
                name: "collection_items");

            migrationBuilder.DropTable(
                name: "email_actions");

            migrationBuilder.DropTable(
                name: "outbox_email_deliveries");

            migrationBuilder.DropTable(
                name: "outbox_messages");

            migrationBuilder.DropTable(
                name: "product_analytics_daily");

            migrationBuilder.DropTable(
                name: "purchases");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "reviews");

            migrationBuilder.DropTable(
                name: "seller_analytics_daily");

            migrationBuilder.DropTable(
                name: "traffic_analytics_daily");

            migrationBuilder.DropTable(
                name: "user_notifications");

            migrationBuilder.DropTable(
                name: "user_social_links");

            migrationBuilder.DropTable(
                name: "asset_processing_jobs");

            migrationBuilder.DropTable(
                name: "tags");

            migrationBuilder.DropTable(
                name: "collections");

            migrationBuilder.DropTable(
                name: "order_lines");

            migrationBuilder.DropTable(
                name: "social_platforms");

            migrationBuilder.DropTable(
                name: "asset_versions");

            migrationBuilder.DropTable(
                name: "orders");

            migrationBuilder.DropTable(
                name: "checkout_intents");

            migrationBuilder.DropTable(
                name: "assets");

            migrationBuilder.DropTable(
                name: "bundle_revisions");

            migrationBuilder.DropTable(
                name: "categories");

            migrationBuilder.DropTable(
                name: "bundles");

            migrationBuilder.DropTable(
                name: "users");
        }
    }
}
