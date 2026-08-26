using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto;

public sealed record ClaimedAssetProcessingJob(
    Guid JobId,
    Guid AssetId,
    Guid AssetVersionId,
    Guid OwnerUserId,
    AssetProcessingJobType Type,
    int DefinitionVersion,
    int AttemptCount,
    int MaxAttempts,
    string Payload,
    string? TraceParent,
    Guid LeaseToken,
    DateTimeOffset AvailableAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt = null
);
