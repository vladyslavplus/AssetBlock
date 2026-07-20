using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
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
        var versionId = Guid.NewGuid();
        const string storageKey = "integration/seed/asset.bin";
        const string fileName = "asset.bin";
        var license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        var now = DateTimeOffset.UtcNow;
        var asset = new Asset
        {
            Id = assetId,
            AuthorId = userId,
            CategoryId = category.Id,
            Title = "Integration seeded asset",
            Description = "Seeded for integration tests.",
            Price = 9.99m,
            StorageKey = storageKey,
            FileName = fileName,
            CreatedAt = now
        };
        var version = new AssetVersion
        {
            Id = versionId,
            AssetId = assetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = storageKey,
            FileName = fileName,
            ContentLength = 1,
            ContentSha256 = new string('0', 64),
            ReleaseNotes = "Initial release",
            LicenseCode = license.Code,
            LicenseTemplateVersion = license.TemplateVersion,
            LicenseDisplayName = license.DisplayName,
            LicenseTerms = license.TermsPlainText,
            CreatedAt = now
        };

        db.Users.Add(user);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        return assetId;
    }
}
