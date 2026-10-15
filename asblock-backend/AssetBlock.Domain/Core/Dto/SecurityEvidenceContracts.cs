using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Dto;

/// <summary>
/// Actual archive-validation evidence for the exact bytes of a version. Unlike the job result
/// summary this carries the verified hash, consumed byte scope, and observed implementation.
/// </summary>
public sealed record ArchiveValidationEvidence(
    string ContentSha256,
    int FileCount,
    long TotalExpandedBytes,
    long ScannedByteCount,
    bool ScopeComplete,
    string ImplementationIdentity);

/// <summary>
/// Actual malware-scan evidence for the exact bytes of a version. Scanner and signature identity
/// describe the observed identity of this specific scan and are never synthesized from the
/// installed version; an unavailable identity is recorded as unavailable.
/// </summary>
public sealed record MalwareScanEvidence(
    string ContentSha256,
    long ScannedByteCount,
    bool ScopeComplete,
    bool IdentityAvailable,
    string? EngineIdentity,
    string? EngineVersion,
    string? SignatureIdentity,
    DateTimeOffset? SignatureObservedAt,
    DateTimeOffset? SignatureValidUntil);

/// <summary>Strictly typed outcome of validating a decrypted analysis response.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CodeAnalysisValidationOutcome
{
    ADMITTED,
    REPLAYED,
    FENCED,
    STALE_INPUT,
    REJECTED,
    BUSY_NOT_STARTED
}
