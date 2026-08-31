using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class AssetProcessingJob : BaseEntity
{
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    public Guid AssetVersionId { get; set; }
    public AssetVersion AssetVersion { get; set; } = null!;

    public AssetProcessingJobType Type { get; set; }

    public int DefinitionVersion { get; set; }

    public AssetProcessingJobStatus Status { get; set; }

    public string Stage { get; set; } = null!;

    public int AttemptCount { get; set; }

    public int MaxAttempts { get; set; }

    public DateTimeOffset AvailableAt { get; set; }

    public string? LeaseOwner { get; set; }

    public Guid? LeaseToken { get; set; }

    public DateTimeOffset? LeaseExpiresAt { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorSummary { get; set; }

    public string? Payload { get; set; }

    public string? Result { get; set; }

    public string? TraceParent { get; set; }
}
