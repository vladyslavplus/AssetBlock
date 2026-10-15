using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>How a finalized report relates to the current selection generation of its version.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeAnalysisReportDisposition
{
    SELECTED,
    HISTORICAL_SUPERSEDED,
    HISTORICAL_STALE_INPUT,
    HISTORICAL_INCOMPLETE
}
