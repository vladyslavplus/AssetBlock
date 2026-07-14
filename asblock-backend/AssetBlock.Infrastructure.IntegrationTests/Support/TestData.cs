using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Persistence;

namespace AssetBlock.Infrastructure.IntegrationTests.Support;

/// <summary>
/// Minimal FK-safe seed helpers for PostgreSQL store tests. No real credentials.
/// </summary>
internal static class TestData
{
    private const string PASSWORD_HASH = "test-password-hash-not-a-real-secret";

    public static User CreateUser(
        string username = "author",
        string email = "author@example.test")
    {
        return new User
        {
            Id = Guid.NewGuid(),
            Username = username,
            Email = email.Trim().ToLowerInvariant(),
            PasswordHash = PASSWORD_HASH,
            Role = AppRoles.USER,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static Category CreateCategory(string name = "Tools", string slug = "tools")
    {
        return new Category
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static Asset CreateAsset(
        Guid authorId,
        Guid categoryId,
        string title = "Sample Asset",
        decimal price = 9.99m,
        string? description = null,
        DateTimeOffset? createdAt = null,
        Guid? id = null)
    {
        return new Asset
        {
            Id = id ?? Guid.NewGuid(),
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = title,
            Description = description,
            Price = price,
            StorageKey = $"assets/{Guid.NewGuid():N}.bin",
            FileName = "package.zip",
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };
    }

    public static Tag CreateTag(string name = "csharp")
    {
        return new Tag
        {
            Id = Guid.NewGuid(),
            Name = name.Trim().ToLowerInvariant(),
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static Purchase CreatePurchase(
        Guid userId,
        Guid assetId,
        DateTimeOffset? purchasedAt = null,
        string? stripePaymentId = null,
        Guid? id = null)
    {
        return new Purchase
        {
            Id = id ?? Guid.NewGuid(),
            UserId = userId,
            AssetId = assetId,
            PurchasedAt = purchasedAt ?? DateTimeOffset.UtcNow,
            StripePaymentId = stripePaymentId,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static Review CreateReview(
        Guid userId,
        Guid assetId,
        int rating = 5,
        string? comment = "Solid asset")
    {
        return new Review
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AssetId = assetId,
            Rating = rating,
            Comment = comment,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static async Task<(User Author, Category Category)> SeedAuthorAndCategory(
        ApplicationDbContext db,
        CancellationToken cancellationToken = default)
    {
        var author = CreateUser();
        var category = CreateCategory();
        db.Users.Add(author);
        db.Categories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return (author, category);
    }
}
