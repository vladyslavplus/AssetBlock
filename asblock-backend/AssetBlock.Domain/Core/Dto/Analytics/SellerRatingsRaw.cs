namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Raw ratings data for a seller as returned by the analytics store.
/// </summary>
public sealed record SellerRatingsRaw(
    double? AverageRating,
    int NewReviews);
