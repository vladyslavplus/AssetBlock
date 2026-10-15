using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AnalysisCapacitySlotState
{
    FREE,
    RESERVED,
    RELEASING,
    RECOVERING
}
