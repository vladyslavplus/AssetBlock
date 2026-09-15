using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Support;

internal static class SimilarAssetsSeed
{
    private static readonly Guid _emptySourceId = Guid.Parse("c1111111-2222-4333-8444-555555555511");
    private static readonly Guid _emptyCategoryId = Guid.Parse("c1111111-2222-4333-8444-555555555512");
    private static readonly Guid _pairSourceId = Guid.Parse("c1111111-2222-4333-8444-555555555521");
    private static readonly Guid _pairPeerId = Guid.Parse("c1111111-2222-4333-8444-555555555522");
    private static readonly Guid _pairCategoryId = Guid.Parse("c1111111-2222-4333-8444-555555555523");

    public static async Task<Guid> EnsureEmptySourceAsync(IServiceScopeFactory scopeFactory)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.Assets.AnyAsync(a => a.Id == _emptySourceId))
        {
            return _emptySourceId;
        }

        User author = await EnsureAuthor(db);
        await EnsureCategory(db, _emptyCategoryId, "Similar empty", "similar-empty");
        await AddReadyAsset(db, _emptySourceId, author.Id, _emptyCategoryId, "Similar empty source");
        await db.SaveChangesAsync();
        return _emptySourceId;
    }

    public static async Task<(Guid SourceId, Guid PeerId)> EnsurePublicPairAsync(IServiceScopeFactory scopeFactory)
    {
        using IServiceScope scope = scopeFactory.CreateScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.Assets.AnyAsync(a => a.Id == _pairSourceId))
        {
            return (_pairSourceId, _pairPeerId);
        }

        User author = await EnsureAuthor(db);
        await EnsureCategory(db, _pairCategoryId, "Similar pair", "similar-pair");
        await AddReadyAsset(db, _pairSourceId, author.Id, _pairCategoryId, "Similar pair source");
        await AddReadyAsset(db, _pairPeerId, author.Id, _pairCategoryId, "Similar pair peer");
        await db.SaveChangesAsync();
        return (_pairSourceId, _pairPeerId);
    }

    private static async Task<User> EnsureAuthor(ApplicationDbContext db)
    {
        User? user = await db.Users.FirstOrDefaultAsync(u => u.Email == "integration.similar@test.local");
        if (user is not null)
        {
            return user;
        }

        user = new User
        {
            Id = Guid.NewGuid(),
            Username = "integration_similar_author",
            Email = "integration.similar@test.local",
            PasswordHash = "na",
            Role = AppRoles.USER
        };
        db.Users.Add(user);
        return user;
    }

    private static async Task EnsureCategory(ApplicationDbContext db, Guid id, string name, string slug)
    {
        if (await db.Categories.AnyAsync(c => c.Id == id))
        {
            return;
        }

        db.Categories.Add(new Category
        {
            Id = id,
            Name = name,
            Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private static Task AddReadyAsset(ApplicationDbContext db, Guid assetId, Guid authorId, Guid categoryId, string title)
    {
        AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.Assets.Add(new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = title,
            Description = "Similar assets integration seed.",
            Price = 11.50m,
            CreatedAt = now
        });
        db.AssetVersions.Add(new AssetVersion
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = $"integration/similar/{assetId:N}.bin",
            FileName = "asset.bin",
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
        });
        return Task.CompletedTask;
    }
}
