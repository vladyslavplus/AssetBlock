using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// A separate re-verification of the exact bytes of a version that is already READY but lacks
/// sufficient security provenance. A generation is a new verification of the same bytes, never a
/// new asset version and never a job definition increment.
/// </summary>
public class SecurityRecheck : BaseEntity
{
    public required Guid AssetId { get; set; }

    public required Guid AssetVersionId { get; set; }

    /// <summary>Exact advertised plaintext hash this recheck verifies.</summary>
    public required string ContentSha256 { get; set; }

    /// <summary>Monotonic per version. Identifies a new verification of unchanged bytes.</summary>
    public required int SecurityGeneration { get; set; }

    public required string SecurityPolicyVersion { get; set; }

    public required int DefinitionVersion { get; set; }

    /// <summary>Digest of the immutable recheck request; identical requests return the same record.</summary>
    public required string OperationDigest { get; set; }

    public required Guid OperationReceiptId { get; set; }

    public SecurityRecheckState State { get; set; } = SecurityRecheckState.QUEUED;

    /// <summary>Set on the single selected generation for this version; null while none is selected.</summary>
    public int? SelectedGeneration { get; set; }

    /// <summary>Linked prior generation created by an explicit terminal retry.</summary>
    public Guid? ParentRecheckId { get; set; }

    public Guid? ArchiveEvidenceId { get; set; }

    public Guid? MalwareEvidenceId { get; set; }

    /// <summary>Security generation recorded on the produced evidence, used to reject stale completions.</summary>
    public int? CompletedGeneration { get; set; }

    public string? ErrorCode { get; set; }

    public string? ErrorSummary { get; set; }

    public DateTimeOffset? RequestedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }

    public Asset Asset { get; set; } = null!;

    public AssetVersion AssetVersion { get; set; } = null!;
}
