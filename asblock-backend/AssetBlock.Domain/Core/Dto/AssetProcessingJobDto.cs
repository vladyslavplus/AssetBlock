using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto;

public sealed record AssetProcessingJobDto(
    Guid Id,
    Guid AssetId,
    Guid AssetVersionId,
    AssetProcessingJobType Type,
    int DefinitionVersion,
    AssetProcessingJobStatus Status,
    string Stage,
    int AttemptCount,
    int MaxAttempts,
    DateTimeOffset AvailableAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorSummary,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt
);
