namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Recomputed exact per-tag affinity totals for one opted-in account over the rolling
/// personalization window. Snapshot values, never incremented logs.
/// </summary>
public sealed class UserTagAffinity
{
    public Guid UserId { get; set; }

    public Guid TagId { get; set; }

    public long Purchases { get; set; }

    public long Reviews { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
