using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Immutable analysis policy identity. Rows are content-addressed by
/// (<see cref="PolicyKey"/>, <see cref="PolicyDigest"/>) so a pinned policy can never be edited in place.
/// </summary>
public class CodeAnalysisPolicyVersion : BaseEntity
{
    /// <summary>Stable protocol policy identifier expected by the analysis service.</summary>
    public required string PolicyKey { get; init; }

    /// <summary>Lowercase 64-character hex SHA-256 over the canonical policy manifest.</summary>
    public required string PolicyDigest { get; init; }

    public required string SchemaName { get; init; }

    /// <summary>Mandatory/optional stage matrix for this policy.</summary>
    public required string StageMatrixJson { get; init; }

    public required string VerifierMode { get; init; }

    public required string VerifierReason { get; init; }

    /// <summary>Observed tool, parser, and runtime identities required by this policy.</summary>
    public required string ToolIdentitiesJson { get; init; }

    public required string LimitsJson { get; init; }

    public required string SecurityValidityJson { get; init; }

    public string? ModelKey { get; init; }

    public string? IndexKey { get; init; }

    public string? ReferenceSnapshotId { get; init; }
}
