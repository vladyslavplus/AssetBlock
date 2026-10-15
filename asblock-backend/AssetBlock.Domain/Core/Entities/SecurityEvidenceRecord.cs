using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Append-only security evidence for an exact content hash. Records what was actually scanned, by
/// which observed implementation, over which byte scope, and when the scanner identity was observed.
/// Never updated in place, so historical evidence survives later generations.
/// </summary>
public class SecurityEvidenceRecord
{
    public required Guid Id { get; init; }

    public required Guid AssetId { get; init; }

    public required Guid AssetVersionId { get; init; }

    /// <summary>Plaintext SHA-256 of the exact bytes covered by this evidence.</summary>
    public required string ContentSha256 { get; init; }

    /// <summary>Null for original initial-upload evidence, set for READY-version rechecks.</summary>
    public Guid? SecurityRecheckId { get; init; }

    public SecurityEvidenceKind EvidenceKind { get; init; }

    public Guid? JobId { get; init; }

    public required int Generation { get; init; }

    public required int DefinitionVersion { get; init; }

    public required SecurityEvidenceOutcome Outcome { get; init; }

    public required string SecurityPolicyVersion { get; init; }

    /// <summary>Observed implementation identity for this specific verification.</summary>
    public string? ImplementationIdentity { get; init; }

    /// <summary>Observed scanner engine identity for this specific scan, when obtainable.</summary>
    public string? EngineIdentity { get; init; }

    public string? EngineVersion { get; init; }

    /// <summary>Observed signature database identity for this specific scan, when obtainable.</summary>
    public string? SignatureIdentity { get; init; }

    public DateTimeOffset? SignatureObservedAt { get; init; }

    public DateTimeOffset? SignatureValidUntil { get; init; }

    /// <summary>False when scanner identity could not be observed; identity is never synthesized.</summary>
    public required bool IdentityAvailable { get; init; }

    /// <summary>Plaintext bytes actually covered by this verification.</summary>
    public required long ScannedByteCount { get; init; }

    public int? ScannedEntryCount { get; init; }

    /// <summary>True when the byte scope was fully consumed to end of stream.</summary>
    public required bool ScopeComplete { get; init; }

    public required DateTimeOffset CompletedAt { get; init; }

    /// <summary>Digest over the persisted evidence identity, used for replay and alteration detection.</summary>
    public required string EvidenceDigest { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
