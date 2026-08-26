using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetVersionProcessingStatus
{
    PENDING_INSPECTION = 0,
    PENDING_MALWARE_SCAN = 1,
    READY = 2,
    REJECTED = 3,
    PROCESSING_FAILED = 4
}
