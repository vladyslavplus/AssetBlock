namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Result of a retention attempt. When <see cref="LockAcquired"/> is false, no deletes ran
/// and the UTC day must not be marked complete.
/// </summary>
public sealed record AnalyticsEventRetentionResult(
    int DeletedCount,
    bool HasBacklog,
    bool LockAcquired = true);
