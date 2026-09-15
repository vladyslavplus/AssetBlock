using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Recommendation measurement kinds recorded in recommendation_events, not analytics_events.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RecommendationEventType
{
    IMPRESSION,
    CLICK
}
