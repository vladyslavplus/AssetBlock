using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Where an engagement event or checkout originated from.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalyticsTrafficSource
{
    CATALOG,
    SEARCH,
    SELLER_PROFILE,
    COLLECTION,
    BUNDLE_PAGE,
    DIRECT_INTERNAL,
    EXTERNAL,
    UNKNOWN
}
