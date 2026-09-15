namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Recomputed exact per-(source, target) click totals for one opted-in account over the
/// rolling personalization window. Snapshot values, never incremented logs.
/// </summary>
public sealed class UserSourceClickAffinity
{
    public Guid UserId { get; set; }

    public Guid SourceAssetId { get; set; }

    public Guid TargetAssetId { get; set; }

    public long Clicks { get; set; }

    public DateTimeOffset LastClickedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
