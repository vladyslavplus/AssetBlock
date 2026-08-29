using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public readonly record struct ReviewCreationResult(
    Review? Review,
    bool IsSuccess,
    bool IsOwnAsset,
    bool IsPurchaseWindowExpired)
{
    public static ReviewCreationResult Success(Review review) => new(review, true, false, false);
    public static ReviewCreationResult CannotReviewOwnAsset() => new(null, false, true, false);
    public static ReviewCreationResult PurchaseWindowExpired() => new(null, false, false, true);
}

public class Review : BaseEntity
{
    public required Guid AssetId { get; set; }
    public required Guid UserId { get; set; }
    public required int Rating { get; set; }
    public string? Comment { get; set; }

    public Asset Asset { get; set; } = null!;
    public User User { get; set; } = null!;

    public static ReviewCreationResult CreateForPurchase(
        Guid assetId,
        Guid authorId,
        Guid reviewerUserId,
        DateTimeOffset purchasedAt,
        int rating,
        string? comment,
        DateTimeOffset now)
    {
        if (authorId == reviewerUserId)
        {
            return ReviewCreationResult.CannotReviewOwnAsset();
        }

        var daysSincePurchase = (now - purchasedAt).TotalDays;
        if (daysSincePurchase > BusinessConstants.MAX_REVIEW_DAYS_AFTER_PURCHASE)
        {
            return ReviewCreationResult.PurchaseWindowExpired();
        }

        var review = new Review
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            UserId = reviewerUserId,
            Rating = rating,
            Comment = comment,
            CreatedAt = now
        };

        return ReviewCreationResult.Success(review);
    }
}
