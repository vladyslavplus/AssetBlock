using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using NpgsqlTypes;

namespace AssetBlock.Infrastructure.Persistence;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Asset> Assets => Set<Asset>();
    public DbSet<AssetVersion> AssetVersions => Set<AssetVersion>();
    public DbSet<Purchase> Purchases => Set<Purchase>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<Review> Reviews => Set<Review>();
    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<AssetTag> AssetTags => Set<AssetTag>();
    public DbSet<UserNotification> UserNotifications => Set<UserNotification>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<EmailAction> EmailActions => Set<EmailAction>();
    public DbSet<CheckoutIntent> CheckoutIntents => Set<CheckoutIntent>();
    public DbSet<CheckoutIntentItem> CheckoutIntentItems => Set<CheckoutIntentItem>();
    public DbSet<CheckoutReservation> CheckoutReservations => Set<CheckoutReservation>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<CollectionItem> CollectionItems => Set<CollectionItem>();
    public DbSet<Bundle> Bundles => Set<Bundle>();
    public DbSet<BundleRevision> BundleRevisions => Set<BundleRevision>();
    public DbSet<BundleRevisionItem> BundleRevisionItems => Set<BundleRevisionItem>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderLine> OrderLines => Set<OrderLine>();
    public DbSet<AnalyticsEvent> AnalyticsEvents => Set<AnalyticsEvent>();
    public DbSet<SellerAnalyticsDaily> SellerAnalyticsDaily => Set<SellerAnalyticsDaily>();
    public DbSet<ProductAnalyticsDaily> ProductAnalyticsDaily => Set<ProductAnalyticsDaily>();
    public DbSet<CollectionAnalyticsDaily> CollectionAnalyticsDaily => Set<CollectionAnalyticsDaily>();
    public DbSet<TrafficAnalyticsDaily> TrafficAnalyticsDaily => Set<TrafficAnalyticsDaily>();
    public DbSet<AssetProcessingJob> AssetProcessingJobs => Set<AssetProcessingJob>();
    public DbSet<AssetArchiveAnalysis> AssetArchiveAnalyses => Set<AssetArchiveAnalysis>();
    public DbSet<AssetListingSuggestion> AssetListingSuggestions => Set<AssetListingSuggestion>();
    public DbSet<OutboxEmailDelivery> OutboxEmailDeliveries => Set<OutboxEmailDelivery>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ApplicationDbContext).Assembly);

        if (Database.ProviderName is null
            || !Database.ProviderName.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        modelBuilder.HasPostgresExtension("pg_trgm");
        modelBuilder.HasDbFunction(
                typeof(PostgresDbFunctions).GetMethod(
                    nameof(PostgresDbFunctions.TrigramsSimilarity),
                    [typeof(string), typeof(string)])!)
            .HasName("similarity");

        ConfigurePostgresAssetSearch(modelBuilder);
        ConfigurePostgresSecondarySearch(modelBuilder);
        ConfigurePostgresAudit(modelBuilder);
        ConfigurePostgresAnalyticsEvents(modelBuilder);
    }

    private static void ConfigurePostgresSecondarySearch(ModelBuilder modelBuilder)
    {
        EntityTypeBuilder<BundleRevision> bundleRevision = modelBuilder.Entity<BundleRevision>();
        bundleRevision.HasIndex(r => r.Title)
            .HasMethod("GIN")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("IX_bundle_revisions_Title_trgm");
        bundleRevision.HasIndex(r => r.Description)
            .HasMethod("GIN")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("IX_bundle_revisions_Description_trgm");

        EntityTypeBuilder<Collection> collection = modelBuilder.Entity<Collection>();
        collection.HasIndex(c => c.Title)
            .HasMethod("GIN")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("IX_collections_Title_trgm");
        collection.HasIndex(c => c.Description)
            .HasMethod("GIN")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("IX_collections_Description_trgm");

        EntityTypeBuilder<Category> category = modelBuilder.Entity<Category>();
        category.HasIndex(c => c.Name)
            .HasMethod("GIN")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("IX_categories_Name_trgm");
        category.HasIndex(c => c.Slug, "IX_categories_Slug_trgm")
            .IsUnique(false)
            .HasMethod("GIN")
            .HasOperators("gin_trgm_ops");
        category.HasIndex(c => c.Description)
            .HasMethod("GIN")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("IX_categories_Description_trgm");
    }

    private static void ConfigurePostgresAssetSearch(ModelBuilder modelBuilder)
    {
        EntityTypeBuilder<Asset> asset = modelBuilder.Entity<Asset>();

        asset.Property<NpgsqlTsVector>(AssetConfiguration.SEARCH_VECTOR_PROPERTY)
            .HasColumnName("search_vector")
            .HasComputedColumnSql(
                """to_tsvector('simple'::regconfig, coalesce("Title", '') || ' ' || coalesce("Description", ''))""",
                stored: true);

        asset.HasIndex(AssetConfiguration.SEARCH_VECTOR_PROPERTY)
            .HasMethod("GIN")
            .HasDatabaseName("IX_assets_search_vector");

        asset.HasIndex(a => a.Title)
            .HasMethod("GIN")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("IX_assets_Title_trgm");

        asset.HasIndex(a => a.Description)
            .HasMethod("GIN")
            .HasOperators("gin_trgm_ops")
            .HasDatabaseName("IX_assets_Description_trgm");

        asset.HasIndex(a => new { a.CreatedAt, a.Id })
            .HasFilter("\"DeletedAt\" IS NULL")
            .HasDatabaseName("IX_assets_catalog_CreatedAt_Id");

        asset.HasIndex(a => new { a.CategoryId, a.CreatedAt, a.Id })
            .HasFilter("\"DeletedAt\" IS NULL")
            .HasDatabaseName("IX_assets_catalog_CategoryId_CreatedAt_Id");

        asset.HasIndex(a => new { a.AuthorId, a.CreatedAt, a.Id })
            .HasFilter("\"DeletedAt\" IS NULL")
            .HasDatabaseName("IX_assets_catalog_AuthorId_CreatedAt_Id");
    }

    private static void ConfigurePostgresAnalyticsEvents(ModelBuilder modelBuilder)
    {
        // BRIN suits an append-only table whose physical order tracks OccurredAt, and it keeps
        // whole-table retention scans cheap without paying btree maintenance on every insert.
        modelBuilder.Entity<AnalyticsEvent>()
            .HasIndex(e => e.OccurredAt)
            .HasMethod("brin")
            .HasDatabaseName("IX_analytics_events_OccurredAt_brin");
    }

    private static void ConfigurePostgresAudit(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLog>()
            .ToTable("audit_logs", table => table.HasCheckConstraint(
                "CK_audit_logs_MetadataJson_Object",
                "\"MetadataJson\" IS NULL OR jsonb_typeof(\"MetadataJson\") = 'object'"));
    }
}
