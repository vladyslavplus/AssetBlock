using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Support;

/// <summary>Seed helpers for asset + version + purchase fixtures used by version/download HTTP tests.</summary>
internal static class AssetVersionsSeed
{
    public static async Task<Guid> GetUserIdAsync(IServiceScopeFactory scopeFactory, string username)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }

    public static async Task<string> GetVersionStorageKeyAsync(IServiceScopeFactory scopeFactory, Guid versionId)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.AssetVersions.AsNoTracking()
            .Where(v => v.Id == versionId)
            .Select(v => v.StorageKey)
            .SingleAsync();
    }

    /// <summary>Creates an asset owned by <paramref name="authorId"/> with <paramref name="versionCount"/> published versions.
    /// Returns the asset id and the ids of every version, in ascending version-number order.</summary>
    public static async Task<(Guid AssetId, List<Guid> VersionIds)> SeedAssetWithVersionsAsync(
        IServiceScopeFactory scopeFactory,
        Guid authorId,
        int versionCount = 1,
        bool deleted = false,
        decimal price = 9.99m)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync()
            ?? throw new InvalidOperationException("No category seeded; expected at least one from migrations/seed data.");

        var assetId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = category.Id,
            Title = $"Versioned asset {assetId:N}",
            Description = "Seeded for version/download integration tests.",
            Price = price,
            CreatedAt = now,
            DeletedAt = deleted ? now : null
        };
        db.Assets.Add(asset);

        var versionIds = new List<Guid>();
        var license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        for (var i = 1; i <= versionCount; i++)
        {
            var versionId = Guid.NewGuid();
            versionIds.Add(versionId);
            db.AssetVersions.Add(new AssetVersion
            {
                Id = versionId,
                AssetId = assetId,
                VersionNumber = i,
                IsCurrent = i == versionCount,
                StorageKey = $"assets/{authorId:N}/{assetId:N}/{versionId:N}.zip",
                FileName = $"v{i}.zip",
                ContentLength = 1,
                ContentSha256 = new string('0', 64),
                ReleaseNotes = $"Version {i} release notes",
                LicenseCode = license.Code,
                LicenseTemplateVersion = license.TemplateVersion,
                LicenseDisplayName = license.DisplayName,
                LicenseTerms = license.TermsPlainText,
                CreatedAt = now.AddMinutes(i)
            });
        }

        await db.SaveChangesAsync();
        return (assetId, versionIds);
    }

    /// <summary>Records a completed purchase (with a satisfying CheckoutIntent) for <paramref name="userId"/>.</summary>
    public static async Task<Guid> SeedPurchaseAsync(
        IServiceScopeFactory scopeFactory,
        Guid userId,
        Guid assetId,
        Guid assetVersionId,
        decimal pricePaid = 9.99m)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var now = DateTimeOffset.UtcNow;
        var checkoutIntentId = Guid.NewGuid();
        var purchaseId = Guid.NewGuid();
        var stripeSessionId = $"cs_test_{Guid.NewGuid():N}";

        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = checkoutIntentId,
            UserId = userId,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            AssetTitle = "Seeded purchase asset",
            UnitAmount = pricePaid,
            Currency = "usd",
            StripeSessionId = stripeSessionId,
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
            CompletedAt = now
        });

        db.Purchases.Add(new Purchase
        {
            Id = purchaseId,
            UserId = userId,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            CheckoutIntentId = checkoutIntentId,
            StripePaymentId = stripeSessionId,
            PricePaid = pricePaid,
            Currency = "usd",
            PurchasedAt = now
        });

        await db.SaveChangesAsync();
        return purchaseId;
    }
}
