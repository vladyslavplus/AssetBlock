using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Support;

internal static class AssetCatalogSeed
{
    public static async Task<Guid> EnsureSampleAssetAsync(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var existing = await db.Assets.AsNoTracking().Select(a => a.Id).FirstOrDefaultAsync();
        if (existing != Guid.Empty)
        {
            return existing;
        }

        var category = await db.Categories.AsNoTracking().FirstAsync();
        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            Username = "integration_asset_author",
            Email = "integration.asset@test.local",
            PasswordHash = "na",
            Role = AppRoles.USER
        };

        var assetId = Guid.NewGuid();
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = userId,
            CategoryId = category.Id,
            Title = "Integration seeded asset",
            Description = "Seeded for integration tests.",
            Price = 9.99m,
            StorageKey = "integration/seed/asset.bin",
            FileName = "asset.bin"
        };

        db.Users.Add(user);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        return assetId;
    }
}
