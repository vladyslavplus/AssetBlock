using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetProcessingJobType
{
    ARCHIVE_INSPECTION,
    MALWARE_SCAN,
    LISTING_COPILOT,
    EMBEDDING_GENERATION
}
