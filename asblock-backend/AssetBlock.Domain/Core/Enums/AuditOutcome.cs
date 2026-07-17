using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

/// <summary>Result of an audited action.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AuditOutcome
{
    SUCCESS,
    FAILURE,
    DENIED
}
