using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetProcessingJobStatus
{
    QUEUED,
    RUNNING,
    RETRY_SCHEDULED,
    SUCCEEDED,
    FAILED,
    CANCELLED
}
