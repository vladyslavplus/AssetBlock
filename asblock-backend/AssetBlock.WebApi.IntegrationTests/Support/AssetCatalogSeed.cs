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
    public const string SAMPLE_TITLE = "Integration seeded asset";
    public const decimal SAMPLE_PRICE = 9.99m;

    /// <summary>Stable id so parallel/shared DB tests never pick another suite's asset.</summary>
    private static readonly Guid _sampleAssetId = Guid.Parse("a1111111-2222-4333-8444-555555555501");

    public static async Task<Guid> EnsureSampleAssetAsync(IServiceScopeFactory scopeFactory)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var existing = await db.Assets.AsNoTracking()
            .Where(a => a.Id == _sampleAssetId || a.Title == SAMPLE_TITLE)
            .Select(a => new { a.Id, a.Title })
            .FirstOrDefaultAsync();

        if (existing is not null)
        {
            return existing.Id;
        }

        var category = await db.Categories.AsNoTracking().FirstAsync();

        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == "integration.asset@test.local");
        if (user is null)
        {
            user = new User
            {
                Id = Guid.NewGuid(),
                Username = "integration_asset_author",
                Email = "integration.asset@test.local",
                PasswordHash = "na",
                Role = AppRoles.USER
            };
            db.Users.Add(user);
        }

        var versionId = Guid.NewGuid();
        const string storageKey = "integration/seed/asset.bin";
        const string fileName = "asset.bin";
        var license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        var now = DateTimeOffset.UtcNow;
        var asset = new Asset
        {
            Id = _sampleAssetId,
            AuthorId = user.Id,
            CategoryId = category.Id,
            Title = SAMPLE_TITLE,
            Description = "Seeded for integration tests.",
            Price = SAMPLE_PRICE,
            CreatedAt = now
        };
        var version = new AssetVersion
        {
            Id = versionId,
            AssetId = _sampleAssetId,
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
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = now,
            CreatedAt = now
        };

        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        return _sampleAssetId;
    }
}
