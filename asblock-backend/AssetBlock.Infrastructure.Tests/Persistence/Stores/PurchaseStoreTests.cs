using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Tests.Infrastructure;

namespace AssetBlock.Infrastructure.Tests.Persistence.Stores;

public sealed class PurchaseStoreTests
{
    [Fact]
    public async Task Add_Exists_GetByStripePaymentId_GetPurchase()
    {
        await using var db = InMemoryDbContextFactory.Create();
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
            StorageKey = "k",
            FileName = "f",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var sut = new PurchaseStore(db);
        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = buyerId,
            AssetId = assetId,
            StripePaymentId = "pi_123",
            PurchasedAt = DateTimeOffset.UtcNow
        };
        await sut.Add(purchase);

        (await sut.Exists(buyerId, assetId)).Should().BeTrue();
        (await sut.GetByStripePaymentId("pi_123"))!.Id.Should().Be(purchase.Id);
        (await sut.GetPurchase(buyerId, assetId))!.Id.Should().Be(purchase.Id);
    }

    [Fact]
    public async Task ListForUser_sets_HasUserReviewed_from_reviews()
    {
        await using var db = InMemoryDbContextFactory.Create();
        var catId = Guid.NewGuid();
        db.Categories.Add(new Category { Id = catId, Name = "C", Slug = "c", CreatedAt = DateTimeOffset.UtcNow });
        var authorId = Guid.NewGuid();
        var buyerId = Guid.NewGuid();
        foreach (var u in new[] { (authorId, "author", "a@a.com"), (buyerId, "buyer", "b@b.com") })
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
        foreach (var aid in new[] { assetReviewedId, assetBareId })
        {
            db.Assets.Add(new Asset
            {
                Id = aid,
                AuthorId = authorId,
                CategoryId = catId,
                Title = aid == assetReviewedId ? "R" : "B",
                StorageKey = "k",
                FileName = "f",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = buyerId,
            AssetId = assetReviewedId,
            StripePaymentId = "pi_r",
            PurchasedAt = DateTimeOffset.UtcNow
        });
        db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = buyerId,
            AssetId = assetBareId,
            StripePaymentId = "pi_b",
            PurchasedAt = DateTimeOffset.UtcNow
        });
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
        var page = await sut.ListForUser(buyerId, request);

        page.Items.Should().HaveCount(2);
        page.Items.Single(i => i.AssetId == assetReviewedId).HasUserReviewed.Should().BeTrue();
        page.Items.Single(i => i.AssetId == assetBareId).HasUserReviewed.Should().BeFalse();
    }

    [Fact]
    public async Task HasPurchasesForAsset_reflects_rows()
    {
        await using var db = InMemoryDbContextFactory.Create();
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
            StorageKey = "k",
            FileName = "f",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var sut = new PurchaseStore(db);
        (await sut.HasPurchasesForAsset(assetId)).Should().BeFalse();

        db.Purchases.Add(new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = buyerId,
            AssetId = assetId,
            StripePaymentId = "pi_x",
            PurchasedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        (await sut.HasPurchasesForAsset(assetId)).Should().BeTrue();
        (await sut.HasPurchasesForAsset(Guid.NewGuid())).Should().BeFalse();
    }
}
