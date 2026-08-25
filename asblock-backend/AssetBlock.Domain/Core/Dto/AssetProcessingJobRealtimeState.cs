using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto;

/// <summary>
/// Server-side projection after committed store transition containing owner ID for SignalR routing.
/// </summary>
public sealed record AssetProcessingJobRealtimeState(
    Guid JobId,
    Guid AssetId,
    Guid AssetVersionId,
    Guid OwnerUserId,
    AssetProcessingJobType Type,
    AssetProcessingJobStatus Status,
    string Stage,
    DateTimeOffset? UpdatedAt
)
{
    public AssetProcessingUpdateMessage ToClientMessage() =>
        new(JobId, AssetId, AssetVersionId, Type, Status, Stage, UpdatedAt);
}

/// <summary>
/// Client-facing real-time message published via SignalR. Never exposes OwnerUserId or internal payload/error details.
/// </summary>
public sealed record AssetProcessingUpdateMessage(
    Guid JobId,
    Guid AssetId,
    Guid AssetVersionId,
    AssetProcessingJobType Type,
    AssetProcessingJobStatus Status,
    string Stage,
    DateTimeOffset? UpdatedAt
);
