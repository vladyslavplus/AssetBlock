using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>
/// Lifecycle of a re-verification of the exact bytes of an already READY version.
/// Independent of the original upload processing states.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityRecheckState
{
    QUEUED,
    INSPECTING,
    SCANNING,
    SUCCEEDED,
    UNSAFE,
    INFECTED,
    ERROR,
    CANCELLED
}
