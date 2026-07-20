using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
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

    public static AssetVersion CreateAssetVersion(
        Guid assetId,
        string? storageKey = null,
        string fileName = "package.zip",
        int versionNumber = 1,
        bool isCurrent = true,
        Guid? id = null)
    {
        var license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        var key = storageKey ?? $"assets/{assetId:N}/v{versionNumber}.bin";
        return new AssetVersion
        {
            Id = id ?? Guid.NewGuid(),
            AssetId = assetId,
            VersionNumber = versionNumber,
            IsCurrent = isCurrent,
            StorageKey = key,
            FileName = fileName,
            ContentLength = 1,
            ContentSha256 = new string('0', 64),
            ReleaseNotes = "Initial release",
            LicenseCode = license.Code,
            LicenseTemplateVersion = license.TemplateVersion,
            LicenseDisplayName = license.DisplayName,
            LicenseTerms = license.TermsPlainText,
            CreatedAt = DateTimeOffset.UtcNow
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
        Guid assetVersionId,
        DateTimeOffset? purchasedAt = null,
        string? stripePaymentId = null,
        Guid? id = null,
        decimal pricePaid = 9.99m,
        string currency = "usd")
    {
        return new Purchase
        {
            Id = id ?? Guid.NewGuid(),
            UserId = userId,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            CheckoutIntentId = Guid.NewGuid(),
            PricePaid = pricePaid,
            Currency = currency,
            PurchasedAt = purchasedAt ?? DateTimeOffset.UtcNow,
            StripePaymentId = stripePaymentId ?? $"test-stripe-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    private static CheckoutIntent CreateCompletedCheckoutIntent(Purchase purchase, string assetTitle = "Test asset")
    {
        return new CheckoutIntent
        {
            Id = purchase.CheckoutIntentId,
            UserId = purchase.UserId,
            AssetId = purchase.AssetId,
            AssetVersionId = purchase.AssetVersionId,
            AssetTitle = assetTitle,
            UnitAmount = purchase.PricePaid,
            Currency = purchase.Currency,
            StripeSessionId = purchase.StripePaymentId,
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = purchase.PurchasedAt,
            ExpiresAt = purchase.PurchasedAt.AddHours(1),
            CompletedAt = purchase.PurchasedAt
        };
    }

    public static void AddCompletedPurchase(ApplicationDbContext db, Purchase purchase, string assetTitle = "Test asset")
    {
        db.CheckoutIntents.Add(CreateCompletedCheckoutIntent(purchase, assetTitle));
        db.Purchases.Add(purchase);
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
