using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Sensitivity of a foreign reference behind a similarity finding.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeAnalysisFindingSensitivity
{
    PUBLIC,
    PRIVATE
}
