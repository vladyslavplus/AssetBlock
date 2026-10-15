using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SecurityEvidenceOutcome
{
    VALID,
    CLEAN,
    UNSAFE,
    INFECTED,
    ERROR
}
