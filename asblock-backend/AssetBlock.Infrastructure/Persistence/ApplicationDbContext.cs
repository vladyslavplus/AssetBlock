using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
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
        ConfigurePostgresAudit(modelBuilder);
    }

    private static void ConfigurePostgresAssetSearch(ModelBuilder modelBuilder)
    {
        var asset = modelBuilder.Entity<Asset>();

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

    private static void ConfigurePostgresAudit(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditLog>()
            .ToTable("audit_logs", table => table.HasCheckConstraint(
                "CK_audit_logs_MetadataJson_Object",
                "\"MetadataJson\" IS NULL OR jsonb_typeof(\"MetadataJson\") = 'object'"));
    }
}
