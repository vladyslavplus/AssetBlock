using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Who issued a persisted analysis admission record. The analysis service never issues these.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeAnalysisAdmissionKind
{
    SERVER_VALIDATED
}
