using System.Text.Json.Serialization;
namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FindingDispositionType { EXPLAINED, FALSE_MATCH, ACCEPTED_WITH_LIMITS, CHANGES_NEEDED, INSUFFICIENT_EVIDENCE }
