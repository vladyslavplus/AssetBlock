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

    public string? InputHash { get; set; }

    public string? ModelKey { get; set; }

    /// <summary>Run this CODE_ANALYSIS job is bound to. Exclusive to that job type.</summary>
    public Guid? AnalysisRunId { get; set; }

    /// <summary>Security recheck these scoped security jobs belong to. Exclusive to security jobs.</summary>
    public Guid? SecurityRecheckId { get; set; }

    /// <summary>
    /// Transport lease token presented to the analysis service. The service requires 64 lowercase
    /// hex characters, so it is persisted explicitly instead of reusing the job lease Guid.
    /// </summary>
    public string? TransportLeaseToken { get; set; }
}
