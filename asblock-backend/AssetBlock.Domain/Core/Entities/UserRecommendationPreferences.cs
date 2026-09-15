namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Per-account opt-in flag for personalized recommendations. Missing row means opted out.
/// Written only on explicit save; never inferred.
/// </summary>
public sealed class UserRecommendationPreferences
{
    public Guid UserId { get; set; }

    public bool IsPersonalized { get; set; }

    /// <summary>Start of the usable input window; inputs older than this are never used (no backfill).</summary>
    public DateTimeOffset? OptedInAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public User User { get; set; } = null!;
}
