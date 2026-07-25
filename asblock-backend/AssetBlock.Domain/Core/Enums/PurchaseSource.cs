using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>How a library entitlement was acquired.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PurchaseSource
{
    ASSET,
    BUNDLE
}
