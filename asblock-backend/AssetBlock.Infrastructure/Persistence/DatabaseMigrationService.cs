using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Persistence;

internal sealed class DatabaseMigrationService(
    IServiceScopeFactory scopeFactory,
    IHostEnvironment environment,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrationService> logger) : IHostedService
{
    private const string DEV_ADMIN_USERNAME = "admin";
    private const string DEV_ADMIN_EMAIL = "admin@admin.com";
    private const string DEV_ADMIN_PASSWORD = "test1234";

    private const string DEMO_VENDOR_USERNAME = "demo-vendor";
    private const string DEMO_VENDOR_EMAIL = "demo.vendor@assetblock.local";
    private const string DEMO_VENDOR_PASSWORD = "demo1234";

    private const string DEMO_REVIEWER_PASSWORD = "demo1234";

    /// <summary>Usernames for buyers who leave demo reviews (password: same as DEMO_REVIEWER_PASSWORD).</summary>
    private static readonly IReadOnlyList<(string Username, string Email)> _demoReviewerAccounts =
        Array.AsReadOnly(new (string Username, string Email)[]
        {
            ("demo-buyer-alice", "alice.buyer@assetblock.local"),
            ("demo-buyer-bob", "bob.buyer@assetblock.local"),
            ("demo-buyer-carol", "carol.buyer@assetblock.local"),
            ("demo-buyer-dan", "dan.buyer@assetblock.local"),
        });

    private const int DEMO_ASSET_COUNT = 12;

    /// <summary>Rotating comments for seeded reviews (English, safe demo text).</summary>
    private static readonly IReadOnlyList<string> _demoReviewComments =
        Array.AsReadOnly([
            "Exactly what I needed for a weekend prototype. Clear structure and easy to extend.",
            "Solid quality. A few rough edges in docs but the code is clean.",
            "Great value — saved me several days of setup. Would buy again from this seller.",
            "Works as advertised. Ran through the included examples without issues.",
            "Nice pack. Wish there were more comments in places, but overall very helpful.",
            "Perfect for our team's starter kit. Onboarded a junior dev in one afternoon.",
            "Good defaults and sensible folder layout. Minor typos only.",
            "Impressive depth for the price. The asset list matched the description.",
            "Straightforward integration. Had one question but nothing blocking.",
            "Clean and well organized. Exactly the kind of boilerplate I was looking for.",
            "Really polished. The README alone was worth it.",
            "Does what it says on the tin. Happy with the purchase."
        ]);

    private static readonly IReadOnlyList<(string Title, string? Description, decimal Price)> _demoAssetBlueprints =
        Array.AsReadOnly(new (string Title, string? Description, decimal Price)[]
        {
            ("[TEST] SaaS dashboard boilerplate", "Auth, charts, and tables — TypeScript-first starter.", 49m),
            ("[TEST] Unity VFX shader pack", "Stylized particles and dissolve effects for URP.", 29m),
            ("[TEST] CLI project scaffolder", "Node.js CLI with pluggable templates and prompts.", 19m),
            ("[TEST] Prisma schema starter kit", "Multi-tenant patterns and soft-delete helpers.", 14m),
            ("[TEST] Tailwind + Radix component kit", "Accessible dark-mode UI primitives.", 79m),
            ("[TEST] GitHub Actions CI pack", "Docker and Node workflows with cache tuning.", 29m),
            ("[TEST] Next.js auth template", "JWT refresh flow and route guards wired up.", 39m),
            ("[TEST] Blender low-poly asset kit", "Modular props with PBR textures.", 24m),
            ("[TEST] Rust async microservice template", "Axum, tracing, and health endpoints.", 34m),
            ("[TEST] Figma design system export", "Tokens and components for handoff.", 22m),
            ("[TEST] Godot 4 platformer starter", "Character controller and tilemap tools.", 27m),
            ("[TEST] Kubernetes Helm snippets", "Common service and ingress patterns.", 18m),
        });

    private static readonly IReadOnlyList<(string Name, string Slug, string? Description)> _defaultCategories =
        Array.AsReadOnly(new (string Name, string Slug, string? Description)[]
        {
            ("Algorithms", "algorithms", "Data structures and algorithms implementations"),
            ("Shaders", "shaders", "Graphics computing shaders and materials"),
            ("UI Components", "ui-components", "Reusable UI elements and controls"),
            ("ML Models", "ml-models", "Pre-trained machine learning models"),
            ("Templates", "templates", "Ready-to-use project templates and boilerplate code"),
            ("Plugins", "plugins", "Extensions and plugins for various platforms"),
            ("Scripts", "scripts", "Automation and utility scripts"),
            ("Audio", "audio", "Sound effects and music tracks for projects")
        });

    private static readonly IReadOnlyList<string> _defaultTags =
        Array.AsReadOnly([
            "react", "vue", "angular", "csharp", "dotnet", "python", "javascript",
            "typescript", "opengl", "vulkan", "webgl", "unity", "unreal-engine",
            "tensorflow", "pytorch", "css", "html", "tailwind", "docker", "kubernetes",
            "aws", "azure", "gcp", "frontend", "backend", "fullstack", "database",
            "sql", "nosql", "game-dev", "ai", "machine-learning"
        ]);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var dbOptions = options.Value;
        if (dbOptions is { AutoMigrate: false, EnsureCreated: false })
        {
            return;
        }

        using var scope = scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        try
        {
            if (dbOptions.EnsureCreated)
            {
                var created = await context.Database.EnsureCreatedAsync(cancellationToken);
                if (created)
                {
                    logger.LogInformation("Database created successfully");
                }
            }
            else if (dbOptions.AutoMigrate)
            {
                var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);
                if (pending.Any())
                {
                    await context.Database.MigrateAsync(cancellationToken);
                    logger.LogInformation("Migrations applied successfully");
                }
            }

            await SeedCategoriesIfEmpty(context, cancellationToken);
            await SeedTagsIfEmpty(context, cancellationToken);

            if (environment.IsDevelopment())
            {
                var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
                await SeedDevAdminIfNeeded(context, passwordHasher, cancellationToken);

                if (dbOptions.SeedDemoAssets)
                {
                    var assetStore = scope.ServiceProvider.GetService<IAssetStore>();
                    var searchService = scope.ServiceProvider.GetService<IAssetSearchService>();
                    if (assetStore is null || searchService is null)
                    {
                        logger.LogWarning(
                            "Database:SeedDemoAssets is true but IAssetStore or IAssetSearchService is not registered; skipping demo catalog seed.");
                    }
                    else
                    {
                        await SeedDemoCatalogAssetsIfEmpty(
                            context,
                            passwordHasher,
                            assetStore,
                            searchService,
                            cancellationToken);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database initialization failed");
            throw;
        }
    }

    private async Task SeedCategoriesIfEmpty(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        if (await context.Categories.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        foreach (var (name, slug, description) in _defaultCategories)
        {
            context.Categories.Add(new Category
            {
                Id = Guid.NewGuid(),
                Name = name,
                Slug = slug,
                Description = description,
                CreatedAt = now
            });
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded {Count} categories", _defaultCategories.Count);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation("Categories already seeded by another instance");
        }
    }

    private async Task SeedTagsIfEmpty(ApplicationDbContext context, CancellationToken cancellationToken)
    {
        if (await context.Tags.AnyAsync(cancellationToken))
        {
            return;
        }

        foreach (var name in _defaultTags)
        {
            context.Tags.Add(new Tag
            {
                Id = Guid.NewGuid(),
                Name = name
            });
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded {Count} tags", _defaultTags.Count);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation("Tags already seeded by another instance");
        }
    }

    private async Task SeedDevAdminIfNeeded(ApplicationDbContext context, IPasswordHasher passwordHasher, CancellationToken cancellationToken)
    {
        var adminExists = await context.Users
            .AnyAsync(u => u.Role == AppRoles.ADMIN, cancellationToken);

        if (adminExists)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var admin = new User
        {
            Id = Guid.NewGuid(),
            Username = DEV_ADMIN_USERNAME,
            Email = DEV_ADMIN_EMAIL,
            PasswordHash = passwordHasher.Hash(DEV_ADMIN_PASSWORD),
            Role = AppRoles.ADMIN,
            CreatedAt = now
        };

        context.Users.Add(admin);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Dev admin seeded -> email: {Email}, password: {Password}", DEV_ADMIN_EMAIL, DEV_ADMIN_PASSWORD);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation("Dev admin already exists (concurrent seed)");
        }
    }

    /// <summary>
    /// Inserts placeholder assets (PostgreSQL) and indexes them in Elasticsearch.
    /// Runs only when the Assets table is empty. Safe to run on every startup — no duplicates.
    /// </summary>
    private async Task SeedDemoCatalogAssetsIfEmpty(
        ApplicationDbContext context,
        IPasswordHasher passwordHasher,
        IAssetStore assetStore,
        IAssetSearchService searchService,
        CancellationToken cancellationToken)
    {
        if (await context.Assets.AnyAsync(cancellationToken))
        {
            return;
        }

        var categories = await context.Categories.AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken);
        if (categories.Count == 0)
        {
            logger.LogWarning("Demo catalog seed skipped: no categories in database.");
            return;
        }

        var tagRows = await context.Tags.AsNoTracking().OrderBy(t => t.Name).ToListAsync(cancellationToken);
        if (tagRows.Count == 0)
        {
            logger.LogWarning("Demo catalog seed skipped: no tags in database.");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var author = await context.Users.FirstOrDefaultAsync(
            u => u.Username == DEMO_VENDOR_USERNAME,
            cancellationToken);
        if (author is null)
        {
            author = new User
            {
                Id = Guid.NewGuid(),
                Username = DEMO_VENDOR_USERNAME,
                Email = DEMO_VENDOR_EMAIL,
                PasswordHash = passwordHasher.Hash(DEMO_VENDOR_PASSWORD),
                Role = AppRoles.USER,
                CreatedAt = now,
                IsPublicProfile = true,
            };
            context.Users.Add(author);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Demo vendor seeded → username: {Username}, password: {Password}",
                    DEMO_VENDOR_USERNAME,
                    DEMO_VENDOR_PASSWORD);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                context.Entry(author).State = EntityState.Detached;
                author = await context.Users.FirstAsync(u => u.Username == DEMO_VENDOR_USERNAME, cancellationToken);
            }
        }

        var insertedIds = new List<Guid>(DEMO_ASSET_COUNT);
        for (var i = 0; i < DEMO_ASSET_COUNT; i++)
        {
            var category = categories[i % categories.Count];
            var (title, description, price) = _demoAssetBlueprints[i];
            var assetId = Guid.NewGuid();
            var asset = new Asset
            {
                Id = assetId,
                AuthorId = author.Id,
                CategoryId = category.Id,
                Title = title,
                Description = description,
                Price = price,
                StorageKey = $"dev/seed/placeholder/{assetId:N}.zip",
                FileName = $"test-asset-{i + 1}.zip",
                CreatedAt = now.AddMinutes(-i),
            };
            context.Assets.Add(asset);
            insertedIds.Add(assetId);

            // Two tags per asset, rotate through seeded tags
            var t0 = tagRows[(i * 2) % tagRows.Count];
            var t1 = tagRows[(i * 2 + 1) % tagRows.Count];
            context.AssetTags.Add(new AssetTag { AssetId = assetId, TagId = t0.Id });
            context.AssetTags.Add(new AssetTag { AssetId = assetId, TagId = t1.Id });
        }

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeded {Count} demo assets in database (titles prefixed with [TEST]).", DEMO_ASSET_COUNT);

        var reviewers = await EnsureDemoReviewers(context, passwordHasher, now, cancellationToken);
        // Include dev admin so seeded catalog gets purchases/reviews from that account too (not only demo-buyer-*).
        var adminForReviews = await context.Users.FirstOrDefaultAsync(
            u => u.Username == DEV_ADMIN_USERNAME,
            cancellationToken);
        if (adminForReviews is not null)
        {
            reviewers = [..reviewers, adminForReviews];
        }

        await SeedDemoPurchasesAndReviews(context, insertedIds, reviewers, now, cancellationToken);

        foreach (var id in insertedIds)
        {
            var loaded = await assetStore.GetById(id, cancellationToken);
            if (loaded is null)
            {
                logger.LogWarning("Demo asset {AssetId} not found after insert; skipping Elasticsearch index.", id);
                continue;
            }

            var averageRating = await GetAverageRatingForAssetAsync(context, id, cancellationToken);
            var document = ToAssetDocument(loaded, averageRating);
            try
            {
                await searchService.IndexAsset(document, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to index demo asset {AssetId} in Elasticsearch.", id);
            }
        }
    }

    /// <summary>Creates demo buyer accounts used for seeded purchases and reviews (idempotent per username).</summary>
    private async Task<List<User>> EnsureDemoReviewers(
        ApplicationDbContext context,
        IPasswordHasher passwordHasher,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var reviewers = new List<User>(capacity: _demoReviewerAccounts.Count);
        foreach (var (username, email) in _demoReviewerAccounts)
        {
            var user = await context.Users.FirstOrDefaultAsync(u => u.Username == username, cancellationToken);
            if (user is not null)
            {
                reviewers.Add(user);
                continue;
            }

            user = new User
            {
                Id = Guid.NewGuid(),
                Username = username,
                Email = email,
                PasswordHash = passwordHasher.Hash(DEMO_REVIEWER_PASSWORD),
                Role = AppRoles.USER,
                CreatedAt = now,
                IsPublicProfile = true,
            };
            context.Users.Add(user);
            try
            {
                await context.SaveChangesAsync(cancellationToken);
                logger.LogInformation(
                    "Demo reviewer seeded -> username: {Username}, password: {Password}",
                    username,
                    DEMO_REVIEWER_PASSWORD);
            }
            catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
            {
                context.Entry(user).State = EntityState.Detached;
                user = await context.Users.FirstAsync(u => u.Username == username, cancellationToken);
            }

            reviewers.Add(user);
        }

        return reviewers;
    }

    /// <summary>
    /// Three purchases and reviews per demo asset, from distinct buyers (matches app rule: one review per user per asset).
    /// Buyers rotate through demo reviewer accounts plus dev admin when present.
    /// </summary>
    private async Task SeedDemoPurchasesAndReviews(
        ApplicationDbContext context,
        IReadOnlyList<Guid> assetIds,
        IReadOnlyList<User> reviewers,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (reviewers.Count < 3)
        {
            logger.LogWarning("Demo review seed skipped: need at least 3 reviewer accounts, got {Count}.", reviewers.Count);
            return;
        }

        const int reviewsPerAsset = 3;
        for (var i = 0; i < assetIds.Count; i++)
        {
            var assetId = assetIds[i];
            for (var j = 0; j < reviewsPerAsset; j++)
            {
                var buyer = reviewers[(i + j) % reviewers.Count];
                var purchaseId = Guid.NewGuid();
                context.Purchases.Add(new Purchase
                {
                    Id = purchaseId,
                    UserId = buyer.Id,
                    AssetId = assetId,
                    PurchasedAt = now.AddDays(-3).AddHours(-j * 2),
                    StripePaymentId = $"seed-demo-{assetId:N}-slot{j}",
                });

                context.Reviews.Add(new Review
                {
                    Id = Guid.NewGuid(),
                    AssetId = assetId,
                    UserId = buyer.Id,
                    Rating = DemoReviewRating(i, j),
                    Comment = _demoReviewComments[(i * reviewsPerAsset + j) % _demoReviewComments.Count],
                    CreatedAt = now.AddDays(-2).AddMinutes(-i * 11 - j * 17),
                });
            }
        }

        await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Seeded demo purchases and reviews ({ReviewsPerAsset} reviews × {AssetCount} assets).",
            reviewsPerAsset,
            assetIds.Count);
    }

    private static int DemoReviewRating(int assetIndex, int reviewSlot)
    {
        // Mostly 4–5 stars; occasional 3 for realism.
        if ((assetIndex + reviewSlot * 3) % 5 == 0)
        {
            return 3;
        }

        return 4 + (assetIndex + reviewSlot) % 2;
    }

    private static async Task<double> GetAverageRatingForAssetAsync(
        ApplicationDbContext context,
        Guid assetId,
        CancellationToken cancellationToken)
    {
        var ratings = await context.Reviews.AsNoTracking()
            .Where(r => r.AssetId == assetId)
            .Select(r => r.Rating)
            .ToListAsync(cancellationToken);
        return ratings.Count == 0 ? 0d : ratings.Average(r => (double)r);
    }

    private static AssetDocument ToAssetDocument(Asset asset, double averageRating)
    {
        var tags = asset.AssetTags
            .Select(at => at.Tag.Name.Trim().ToLowerInvariant())
            .Where(n => n.Length > 0)
            .ToList();

        return new AssetDocument
        {
            Id = asset.Id,
            Title = asset.Title,
            Description = asset.Description,
            Price = asset.Price,
            CategoryId = asset.CategoryId,
            CategoryName = asset.Category.Name,
            CategorySlug = asset.Category.Slug,
            AuthorId = asset.AuthorId,
            AuthorUsername = asset.Author.Username,
            StorageKey = asset.StorageKey,
            Tags = tags,
            CreatedAt = asset.CreatedAt,
            AverageRating = averageRating,
        };
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

