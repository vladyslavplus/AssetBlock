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
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseMigrationService> logger) : IHostedService
{
    private static readonly (string Name, string Slug, string? Description)[] _defaultCategories =
    [
        ("Algorithms", "algorithms", null),
        ("Shaders", "shaders", null),
        ("UI Components", "ui-components", null),
        ("ML Models", "ml-models", null)
    ];

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
                await context.Database.MigrateAsync(cancellationToken);
                logger.LogInformation("Migrations applied successfully");
            }

            await SeedCategoriesIfEmpty(context, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Database migration or ensure-created failed");
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
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Seeded {Count} categories", _defaultCategories.Length);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation("Categories already seeded by another instance");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
