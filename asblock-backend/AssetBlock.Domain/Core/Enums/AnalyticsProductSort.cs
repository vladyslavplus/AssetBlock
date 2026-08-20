using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalyticsProductSort
{
    REVENUE,
    ORDERS,
    UNITS,
    RATING,
    RECENT
}
