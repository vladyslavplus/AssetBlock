using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// One immutable analysis attempt-chain bound to a single exact input tuple. Retries reuse the same
/// run id and input digest with a fresh reservation; an explicit retry after a terminal failure
/// creates a new linked run.
/// </summary>
public class CodeAnalysisRun : BaseEntity
{
    public required Guid AssetId { get; set; }

    public required Guid AssetVersionId { get; set; }

    public Guid? AssetDraftWorkspaceId { get; set; }

    /// <summary>Exact advertised plaintext hash analyzed by this run.</summary>
    public required string ContentSha256 { get; set; }

    public required Guid PolicyVersionId { get; set; }

    public required string PolicyDigest { get; set; }

    public Guid? DeclarationRevisionId { get; set; }

    public required string DeclarationDigest { get; set; }

    public Guid? MaterialRevisionId { get; set; }

    public required string MaterialDigest { get; set; }

    /// <summary>
    /// Immutable computational identity of the analysis input, excluding the execution reservation.
    /// A fresh lease for the same run keeps the same digest.
    /// </summary>
    public required string InputDigest { get; set; }

    /// <summary>Generation of the selected analysis binding this run was created for.</summary>
    public required int SelectionGeneration { get; set; }

    /// <summary>Linked prior run created by an explicit retry after a terminal failure.</summary>
    public Guid? ParentRunId { get; set; }

    public Guid? JobId { get; set; }

    public Guid? SecurityRecheckId { get; set; }

    public required Guid ArchiveEvidenceId { get; set; }

    public required Guid MalwareEvidenceId { get; set; }

    public required int SecurityGeneration { get; set; }

    /// <summary>Shared capacity service key this run executes against.</summary>
    public required string ServiceKey { get; set; }

    /// <summary>
    /// Explicit persisted transport binding for the analysis service. The service requires 64
    /// lowercase hex characters, so it is never the job lease Guid formatted as a string.
    /// </summary>
    public string? TransportLeaseToken { get; set; }

    public int? ReservationGeneration { get; set; }

    /// <summary>Immutable execution deadline; lease renewal never extends it.</summary>
    public DateTimeOffset? HardDeadlineUtc { get; set; }

    public CodeAnalysisRunState State { get; set; } = CodeAnalysisRunState.QUEUED;

    public CodeAnalysisReportDisposition? Disposition { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorSummary { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Asset Asset { get; set; } = null!;

    public AssetVersion AssetVersion { get; set; } = null!;
}
