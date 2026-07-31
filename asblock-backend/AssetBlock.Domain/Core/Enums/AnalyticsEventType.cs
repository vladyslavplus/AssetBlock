using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Engagement telemetry event kinds recorded in analytics_events.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalyticsEventType
{
    ASSET_VIEW,
    BUNDLE_VIEW,
    COLLECTION_VIEW,
    COLLECTION_ITEM_CLICK,
    DOWNLOAD_REQUESTED
}
