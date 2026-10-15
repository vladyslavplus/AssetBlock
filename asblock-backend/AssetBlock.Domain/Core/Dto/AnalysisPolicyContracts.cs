namespace AssetBlock.Domain.Core.Dto;

/// <summary>
/// The approved analysis policy manifest a deployment publishes. Everything the analysis runtime
/// must actually run with is declared here: the stage matrix, the observed tool, parser and runtime
/// identities, the verifier mode, the operation limits and the security validity window. The record
/// is content-addressed by (PolicyKey, PolicyDigest), so it is published once and never edited.
/// </summary>
public sealed record AnalysisPolicyManifest(
    string PolicyKey,
    string PolicyDigest,
    string SchemaName,
    string StageMatrixJson,
    string VerifierMode,
    string VerifierReason,
    string ToolIdentitiesJson,
    string LimitsJson,
    string SecurityValidityJson,
    string? ModelKey = null,
    string? IndexKey = null,
    string? ReferenceSnapshotId = null);

/// <summary>
/// Outcome of publishing an approved policy manifest.
/// <c>Published</c> is returned for a fresh insert and for an identical row that already exists, so
/// publication is idempotent. <c>Conflict</c> is returned when the manifest cannot be published
/// without violating immutability: either the key already carries a different approved digest, or
/// the same (key, digest) pair already carries different content. <c>Rejected</c> is returned for a
/// manifest that is not a publishable identity at all, and nothing is written for it.
/// </summary>
public enum AnalysisPolicyPublishOutcome
{
    PUBLISHED,
    CONFLICT,
    REJECTED
}

/// <summary>Result of one content-addressed, insert-only policy publication attempt.</summary>
public sealed record AnalysisPolicyPublishResult(
    AnalysisPolicyPublishOutcome Outcome,
    Guid PolicyVersionId,
    string Reason)
{
    public bool Published => Outcome == AnalysisPolicyPublishOutcome.PUBLISHED;
}
