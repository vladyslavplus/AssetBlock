namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsCollectionTopAsset(
    Guid AssetId,
    string Title,
    long Clicks);
