namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Daily recommendation impression/click counts without visitor, session, or actor identifiers.
/// Not a unique-visitor or billing aggregate.
/// </summary>
public sealed class RecommendationDaily
{
    public DateOnly DayUtc { get; set; }

    public Guid SourceAssetId { get; set; }

    public Guid TargetAssetId { get; set; }

    public string RankingVersion { get; set; } = string.Empty;

    public long ImpressionCount { get; set; }

    public long ClickCount { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
