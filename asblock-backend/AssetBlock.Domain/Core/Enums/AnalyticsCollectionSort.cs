using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalyticsCollectionSort
{
    VIEWS,
    CLICKS,
    ATTRIBUTED_REVENUE,
    RECENT
}
