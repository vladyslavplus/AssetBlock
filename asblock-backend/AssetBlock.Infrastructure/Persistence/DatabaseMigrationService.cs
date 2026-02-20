using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
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
    private const string DEV_ADMIN_EMAIL = "admin@admin.com";
    private const string DEV_ADMIN_PASSWORD = "test1234";

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
                var pending = await context.Database.GetPendingMigrationsAsync(cancellationToken);
                if (pending.Any())
                {
                    await context.Database.MigrateAsync(cancellationToken);
                    logger.LogInformation("Migrations applied successfully");
                }
            }

            await SeedCategoriesIfEmpty(context, cancellationToken);

            if (environment.IsDevelopment())
            {
                var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
                await SeedDevAdminIfNeeded(context, passwordHasher, cancellationToken);
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
            logger.LogInformation("Seeded {Count} categories", _defaultCategories.Length);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation("Categories already seeded by another instance");
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
            Email = DEV_ADMIN_EMAIL,
            PasswordHash = passwordHasher.Hash(DEV_ADMIN_PASSWORD),
            Role = AppRoles.ADMIN,
            CreatedAt = now
        };

        context.Users.Add(admin);
        try
        {
            await context.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Dev admin seeded → email: {Email}, password: {Password}", DEV_ADMIN_EMAIL, DEV_ADMIN_PASSWORD);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            logger.LogInformation("Dev admin already exists (concurrent seed)");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

