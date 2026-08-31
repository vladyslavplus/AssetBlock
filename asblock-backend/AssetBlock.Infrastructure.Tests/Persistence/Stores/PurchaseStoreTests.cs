using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Tests.Infrastructure;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class PurchaseStoreTests
{
    [Fact]
    public async Task Add_Exists_GetPurchase()
    {
        await using ApplicationDbContext db = InMemoryDbContextFactory.Create();
        var catId = Guid.NewGuid();
        db.Categories.Add(new Category { Id = catId, Name = "C", Slug = "c", CreatedAt = DateTimeOffset.UtcNow });
        var userId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = userId,
            Username = "auth",
            Email = "a@a.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.Users.Add(new User
        {
            Id = buyerId,
            Username = "buy",
            Email = "b@b.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var assetId = Guid.NewGuid();
        db.Assets.Add(new Asset
        {
            Id = assetId,
            AuthorId = userId,
            CategoryId = catId,
            Title = "A",
            CreatedAt = DateTimeOffset.UtcNow
        });
        var versionId = Guid.NewGuid();
        db.AssetVersions.Add(CreateAssetVersion(assetId, versionId));
        await db.SaveChangesAsync();

        var sut = new PurchaseStore(db);
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = buyerId,
            AssetId = assetId,
            AssetVersionId = versionId,
            OrderLineId = Guid.NewGuid(),
            PurchasedAt = DateTimeOffset.UtcNow
        };
        await sut.Add(purchase);

        (await sut.Exists(buyerId, assetId)).Should().BeTrue();
        (await sut.GetPurchase(buyerId, assetId))!.Id.Should().Be(purchase.Id);
    }

    [Fact]
    public async Task ListForUser_sets_HasUserReviewed_from_reviews()
    {
        await using ApplicationDbContext db = InMemoryDbContextFactory.Create();
        var catId = Guid.NewGuid();
        db.Categories.Add(new Category { Id = catId, Name = "C", Slug = "c", CreatedAt = DateTimeOffset.UtcNow });
        var authorId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        foreach ((Guid, string, string) u in new[] { (authorId, "author", "a@a.com"), (buyerId, "buyer", "b@b.com") })
        {
            db.Users.Add(new User
            {
                Id = u.Item1,
                Username = u.Item2,
                Email = u.Item3,
                PasswordHash = "h",
                Role = AppRoles.USER,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        var assetReviewedId = Guid.NewGuid();
        var assetBareId = Guid.NewGuid();
        foreach (Guid aid in new[] { assetReviewedId, assetBareId })
        {
            db.Assets.Add(new Asset
            {
                Id = aid,
                AuthorId = authorId,
                CategoryId = catId,
                Title = aid == assetReviewedId ? "R" : "B",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        var reviewedVersionId = Guid.NewGuid();
        var bareVersionId = Guid.NewGuid();
        db.AssetVersions.Add(CreateAssetVersion(assetReviewedId, reviewedVersionId));
        db.AssetVersions.Add(CreateAssetVersion(assetBareId, bareVersionId));

        SeedCompletedPurchase(db, buyerId, authorId, assetReviewedId, reviewedVersionId, "R");
        SeedCompletedPurchase(db, buyerId, authorId, assetBareId, bareVersionId, "B");
        db.Reviews.Add(new Review
        {
            Id = Guid.NewGuid(),
            AssetId = assetReviewedId,
            UserId = buyerId,
            Rating = 5,
            Comment = "ok",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new PurchaseStore(db);
        var request = new ListMyPurchasesRequest { Page = 1, PageSize = 20, SortDirection = SortDirection.DESC };
        PagedResult<PurchaseLibraryItemDto> page = await sut.ListForUser(buyerId, request);

        page.Items.Should().HaveCount(2);
        page.Items.Single(i => i.AssetId == assetReviewedId).HasUserReviewed.Should().BeTrue();
        page.Items.Single(i => i.AssetId == assetBareId).HasUserReviewed.Should().BeFalse();
    }

    [Fact]
    public async Task HasPurchasesForAsset_reflects_rows()
    {
        await using ApplicationDbContext db = InMemoryDbContextFactory.Create();
        var catId = Guid.NewGuid();
        db.Categories.Add(new Category { Id = catId, Name = "C", Slug = "c", CreatedAt = DateTimeOffset.UtcNow });
        var authorId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        db.Users.Add(new User
        {
            Id = authorId,
            Username = "auth",
            Email = "a@a.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Users.Add(new User
        {
            Id = buyerId,
            Username = "buy",
            Email = "b@b.com",
            PasswordHash = "h",
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var assetId = Guid.NewGuid();
        db.Assets.Add(new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = catId,
            Title = "A",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var versionId = Guid.NewGuid();
        db.AssetVersions.Add(CreateAssetVersion(assetId, versionId));
        await db.SaveChangesAsync();

        var sut = new PurchaseStore(db);
        (await sut.HasPurchasesForAsset(assetId)).Should().BeFalse();

        db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = buyerId,
            AssetId = assetId,
            AssetVersionId = versionId,
            OrderLineId = Guid.NewGuid(),
            PurchasedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        (await sut.HasPurchasesForAsset(assetId)).Should().BeTrue();
        (await sut.HasPurchasesForAsset(Guid.NewGuid())).Should().BeFalse();
    }

    private static void SeedCompletedPurchase(
        ApplicationDbContext db,
        Guid buyerId,
        Guid sellerId,
        Guid assetId,
        Guid assetVersionId,
        string title)
    {
        var intentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyerId,
            AssetId = assetId,
            ProductTitle = title,
            AmountTotal = 9.99m,
            Currency = "usd",
            StripeSessionId = $"cs_{lineId:N}",
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
            CompletedAt = now
        });
        db.CheckoutIntentItems.Add(new CheckoutIntentItem
        {
            Id = Guid.NewGuid(),
            CheckoutIntentId = intentId,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            SellerId = sellerId,
            Position = 1,
            AssetTitleSnapshot = title,
            VersionNumber = 1,
            ListPrice = 9.99m,
            AllocatedPrice = 9.99m,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "terms"
        });
        db.Orders.Add(new Order
        {
            Id = orderId,
            UserId = buyerId,
            CheckoutIntentId = intentId,
            AssetId = assetId,
            ProductTitle = title,
            StripeSessionId = $"cs_{lineId:N}",
            AmountPaid = 9.99m,
            Currency = "usd",
            PurchasedAt = now
        });
        db.OrderLines.Add(new OrderLine
        {
            Id = lineId,
            OrderId = orderId,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            SellerId = sellerId,
            Position = 1,
            AssetTitleSnapshot = title,
            VersionNumber = 1,
            ListPrice = 9.99m,
            PricePaid = 9.99m,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "terms"
        });
        db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = buyerId,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            OrderLineId = lineId,
            PurchasedAt = now
        });
    }

    private static AssetVersion CreateAssetVersion(Guid assetId, Guid versionId) => new()
    {
        Id = versionId,
        AssetId = assetId,
        VersionNumber = 1,
        IsCurrent = true,
        StorageKey = "k",
        FileName = "f",
        ContentLength = 1,
        ContentSha256 = new string('0', 64),
        ReleaseNotes = "Initial release",
        LicenseCode = AssetLicenseCode.PERSONAL,
        LicenseTemplateVersion = "1.0",
        LicenseDisplayName = "Personal",
        LicenseTerms = "terms",
        ProcessingStatus = AssetVersionProcessingStatus.READY,
        ProcessingUpdatedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
