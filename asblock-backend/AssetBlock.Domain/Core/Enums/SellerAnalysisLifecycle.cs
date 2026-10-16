using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Enums;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SellerAnalysisLifecycle
{
    NOT_AVAILABLE,
    QUEUED,
    RUNNING,
    COMPLETED,
    FAILED,
    CANCELLED
}
