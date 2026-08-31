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
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow
        };
    }

    public static AssetVersion CreateAssetVersion(
        Guid assetId,
        string? storageKey = null,
        string fileName = "package.zip",
        int versionNumber = 1,
        bool isCurrent = true,
        Guid? id = null,
        AssetVersionProcessingStatus? processingStatus = null)
    {
        AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        var key = storageKey ?? $"assets/{assetId:N}/v{versionNumber}.bin";
        AssetVersionProcessingStatus status = processingStatus ?? (isCurrent ? AssetVersionProcessingStatus.READY : AssetVersionProcessingStatus.PENDING_INSPECTION);
        DateTimeOffset now = DateTimeOffset.UtcNow;
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
            ProcessingStatus = status,
            ProcessingUpdatedAt = now,
            CreatedAt = now
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
        Guid? id = null,
        Guid? orderLineId = null)
    {
        return new Purchase
        {
            Id = id ?? Guid.NewGuid(),
            UserId = userId,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            OrderLineId = orderLineId ?? Guid.NewGuid(),
            PurchasedAt = purchasedAt ?? DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    /// <summary>
    /// Seeds a completed single-asset checkout intent, order, and order line (no purchase).
    /// </summary>
    public static void AddCompletedCheckoutIntent(
        ApplicationDbContext db,
        Purchase purchase,
        string assetTitle,
        Guid sellerId,
        decimal pricePaid = 9.99m,
        string currency = "usd",
        string? stripeSessionId = null)
    {
        DateTimeOffset now = purchase.PurchasedAt;
        var intentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        Guid orderLineId = purchase.OrderLineId;
        var sessionId = stripeSessionId ?? $"test-stripe-{Guid.NewGuid():N}";

        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = purchase.UserId,
            AssetId = purchase.AssetId,
            ProductTitle = assetTitle,
            AmountTotal = pricePaid,
            Currency = currency,
            StripeSessionId = sessionId,
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
            CompletedAt = now
        });
        db.CheckoutIntentItems.Add(new CheckoutIntentItem
        {
            Id = Guid.NewGuid(),
            CheckoutIntentId = intentId,
            AssetId = purchase.AssetId,
            AssetVersionId = purchase.AssetVersionId,
            SellerId = sellerId,
            Position = 1,
            AssetTitleSnapshot = assetTitle,
            VersionNumber = 1,
            ListPrice = pricePaid,
            AllocatedPrice = pricePaid,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        });
        db.Orders.Add(new Order
        {
            Id = orderId,
            UserId = purchase.UserId,
            CheckoutIntentId = intentId,
            AssetId = purchase.AssetId,
            ProductTitle = assetTitle,
            StripeSessionId = sessionId,
            AmountPaid = pricePaid,
            Currency = currency,
            PurchasedAt = now,
            CreatedAt = now
        });
        db.OrderLines.Add(new OrderLine
        {
            Id = orderLineId,
            OrderId = orderId,
            AssetId = purchase.AssetId,
            AssetVersionId = purchase.AssetVersionId,
            SellerId = sellerId,
            Position = 1,
            AssetTitleSnapshot = assetTitle,
            VersionNumber = 1,
            ListPrice = pricePaid,
            PricePaid = pricePaid,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        });
    }

    /// <summary>
    /// Seeds a completed single-asset checkout intent, order, order line, and purchase for library / entitlement tests.
    /// </summary>
    public static void AddCompletedPurchase(
        ApplicationDbContext db,
        Purchase purchase,
        string assetTitle,
        Guid sellerId,
        decimal pricePaid = 9.99m,
        string currency = "usd",
        string? stripeSessionId = null)
    {
        AddCompletedCheckoutIntent(db, purchase, assetTitle, sellerId, pricePaid, currency, stripeSessionId);
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
        User author = CreateUser();
        Category category = CreateCategory();
        db.Users.Add(author);
        db.Categories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return (author, category);
    }

    public static Collection CreateCollection(
        Guid sellerId,
        string title = "Editorial Picks",
        CollectionStatus status = CollectionStatus.DRAFT,
        Guid? id = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new Collection
        {
            Id = id ?? Guid.NewGuid(),
            SellerId = sellerId,
            Title = title,
            Description = "Seeded collection",
            Status = status,
            PublishedAt = status == CollectionStatus.PUBLISHED ? now : null,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static CollectionItem CreateCollectionItem(Guid collectionId, Guid assetId, int position) =>
        new()
        {
            CollectionId = collectionId,
            AssetId = assetId,
            Position = position,
            CreatedAt = DateTimeOffset.UtcNow
        };

    public static CheckoutReservation CreateReservation(
        Guid checkoutIntentId,
        Guid userId,
        Guid assetId,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? createdAt = null)
    {
        DateTimeOffset created = createdAt ?? DateTimeOffset.UtcNow;
        return new CheckoutReservation
        {
            Id = Guid.NewGuid(),
            CheckoutIntentId = checkoutIntentId,
            UserId = userId,
            AssetId = assetId,
            CreatedAt = created,
            ExpiresAt = expiresAt ?? created.AddHours(1)
        };
    }
}
