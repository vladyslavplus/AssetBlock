using AssetBlock.Domain.Core.Constants;
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
}
