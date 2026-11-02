using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeAnalysisStageOutcome
{
    SUCCEEDED,
    FAILED,
    NOT_APPLICABLE
}
