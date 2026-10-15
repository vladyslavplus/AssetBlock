using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Exact-version safety restriction recorded after an actual unsafe or infected recheck.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetVersionSafetyRestriction
{
    NONE,
    UNSAFE_ARCHIVE,
    INFECTED,
    INTEGRITY_MISMATCH
}
