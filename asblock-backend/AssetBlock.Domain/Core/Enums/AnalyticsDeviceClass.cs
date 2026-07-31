using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Coarse device bucket derived client-side; never a device fingerprint.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalyticsDeviceClass
{
    MOBILE,
    TABLET,
    DESKTOP,
    UNKNOWN
}
