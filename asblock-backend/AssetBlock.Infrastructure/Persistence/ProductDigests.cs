using System.Globalization;
using System.Text.Json;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.Persistence;

/// <summary>
/// Canonical digests that identify persisted security, analysis input and report evidence.
/// The analysis input digest mirrors the analysis service's computational identity exactly and
/// deliberately excludes the execution reservation, so a fresh lease keeps the same digest.
/// </summary>
internal static class ProductDigests
{
    /// <summary>Schema identity shared with the analysis service report contract.</summary>
    public const string ANALYSIS_SCHEMA = "code-analysis-v1";

    private const string EVIDENCE_SCHEMA = "security-evidence-v1";
    private const string RECHECK_SCHEMA = "security-recheck-v1";

    public static string BuildArchiveEvidenceDigest(
        Guid assetId,
        Guid assetVersionId,
        ArchiveValidationEvidence evidence,
        int generation,
        int definitionVersion,
        string securityPolicyVersion,
        DateTimeOffset completedAt) =>
        CanonicalJson.DigestOf(CanonicalValue.Object(
            ("schema", CanonicalValue.Text(EVIDENCE_SCHEMA)),
            ("kind", CanonicalValue.Text(nameof(SecurityEvidenceKind.ARCHIVE_VALIDATION))),
            ("assetId", CanonicalValue.Text(assetId.ToString("D"))),
            ("assetVersionId", CanonicalValue.Text(assetVersionId.ToString("D"))),
            ("contentSha256", CanonicalValue.Text(evidence.ContentSha256)),
            ("generation", CanonicalValue.Number(generation)),
            ("definitionVersion", CanonicalValue.Number(definitionVersion)),
            ("outcome", CanonicalValue.Text(nameof(SecurityEvidenceOutcome.VALID))),
            ("securityPolicyVersion", CanonicalValue.Text(securityPolicyVersion)),
            ("implementationIdentity", CanonicalValue.Text(evidence.ImplementationIdentity)),
            ("fileCount", CanonicalValue.Number(evidence.FileCount)),
            ("totalExpandedBytes", CanonicalValue.Number(evidence.TotalExpandedBytes)),
            ("scannedByteCount", CanonicalValue.Number(evidence.ScannedByteCount)),
            ("scopeComplete", CanonicalValue.Bool(evidence.ScopeComplete)),
            ("completedAt", CanonicalValue.Text(Timestamp(completedAt)))));

    public static string BuildMalwareEvidenceDigest(
        Guid assetId,
        Guid assetVersionId,
        MalwareScanEvidence evidence,
        int generation,
        int definitionVersion,
        string securityPolicyVersion,
        DateTimeOffset completedAt) =>
        CanonicalJson.DigestOf(CanonicalValue.Object(
            ("schema", CanonicalValue.Text(EVIDENCE_SCHEMA)),
            ("kind", CanonicalValue.Text(nameof(SecurityEvidenceKind.MALWARE_SCAN))),
            ("assetId", CanonicalValue.Text(assetId.ToString("D"))),
            ("assetVersionId", CanonicalValue.Text(assetVersionId.ToString("D"))),
            ("contentSha256", CanonicalValue.Text(evidence.ContentSha256)),
            ("generation", CanonicalValue.Number(generation)),
            ("definitionVersion", CanonicalValue.Number(definitionVersion)),
            ("outcome", CanonicalValue.Text(nameof(SecurityEvidenceOutcome.CLEAN))),
            ("securityPolicyVersion", CanonicalValue.Text(securityPolicyVersion)),
            ("identityAvailable", CanonicalValue.Bool(evidence.IdentityAvailable)),
            ("engineIdentity", NullableText(evidence.EngineIdentity)),
            ("engineVersion", NullableText(evidence.EngineVersion)),
            ("signatureIdentity", NullableText(evidence.SignatureIdentity)),
            ("signatureObservedAt", NullableTimestamp(evidence.SignatureObservedAt)),
            ("signatureValidUntil", NullableTimestamp(evidence.SignatureValidUntil)),
            ("scannedByteCount", CanonicalValue.Number(evidence.ScannedByteCount)),
            ("scopeComplete", CanonicalValue.Bool(evidence.ScopeComplete)),
            ("completedAt", CanonicalValue.Text(Timestamp(completedAt)))));

    /// <summary>
    /// Immutable computational identity of an analysis input. Mirrors the analysis service digest
    /// field for field and intentionally excludes the reservation, so retries reuse it.
    /// </summary>
    public static string BuildAnalysisInputDigest(
        Guid runId,
        AnalysisInputTuple tuple,
        IReadOnlyList<DeclaredLicenseProjection> declaredLicenses,
        ReferenceProjection? reference)
    {
        var licenseItems = new List<CanonicalValue>(declaredLicenses.Count);
        foreach (DeclaredLicenseProjection license in declaredLicenses)
        {
            licenseItems.Add(CanonicalValue.Object(
                ("scopePath", CanonicalValue.Text(license.ScopePath)),
                ("spdx", CanonicalValue.Text(license.Spdx))));
        }

        CanonicalValue referenceValue = reference is null
            ? CanonicalValue.Null()
            : CanonicalValue.Object(
                ("snapshotId", CanonicalValue.Text(reference.SnapshotId)),
                ("modelKey", CanonicalValue.Text(reference.ModelKey)),
                ("indexKey", CanonicalValue.Text(reference.IndexKey)));

        return CanonicalJson.DigestOf(CanonicalValue.Object(
            ("schema", CanonicalValue.Text(ANALYSIS_SCHEMA)),
            ("runId", CanonicalValue.Text(runId.ToString("D"))),
            ("assetId", CanonicalValue.Text(tuple.AssetId.ToString("D"))),
            ("versionId", CanonicalValue.Text(tuple.AssetVersionId.ToString("D"))),
            ("contentSha256", CanonicalValue.Text(tuple.ContentSha256)),
            ("declarationDigest", CanonicalValue.Text(tuple.DeclarationDigest)),
            ("declaredLicenses", CanonicalValue.Array(licenseItems)),
            ("materialDigest", CanonicalValue.Text(tuple.MaterialDigest)),
            ("policyDigest", CanonicalValue.Text(tuple.PolicyDigest)),
            ("securityEvidence", CanonicalValue.Object(
                ("contentSha256", CanonicalValue.Text(tuple.ContentSha256)),
                ("archiveEvidenceId", CanonicalValue.Text(tuple.ArchiveEvidenceId.ToString("D"))),
                ("malwareEvidenceId", CanonicalValue.Text(tuple.MalwareEvidenceId.ToString("D"))),
                ("securityGeneration", CanonicalValue.Number(tuple.SecurityGeneration)))),
            ("reference", referenceValue)));
    }

    public static string BuildRecheckOperationDigest(
        Guid assetId,
        Guid assetVersionId,
        string contentSha256,
        string securityPolicyVersion,
        int definitionVersion) =>
        CanonicalJson.DigestOf(CanonicalValue.Object(
            ("schema", CanonicalValue.Text(RECHECK_SCHEMA)),
            ("assetId", CanonicalValue.Text(assetId.ToString("D"))),
            ("assetVersionId", CanonicalValue.Text(assetVersionId.ToString("D"))),
            ("contentSha256", CanonicalValue.Text(contentSha256)),
            ("securityPolicyVersion", CanonicalValue.Text(securityPolicyVersion)),
            ("definitionVersion", CanonicalValue.Number(definitionVersion))));

    public static string BuildReportDigest(JsonElement admittedBody) => CanonicalJson.Sha256Hex(admittedBody);

    /// <summary>Stable digest for an absent declaration or material projection.</summary>
    public static string EmptyProjectionDigest(string projectionName) =>
        CanonicalJson.DigestOf(CanonicalValue.Object(
            ("schema", CanonicalValue.Text("empty-projection-v1")),
            ("projection", CanonicalValue.Text(projectionName))));

    public static string BuildTokenDigest(string token) => CanonicalJson.Sha256Hex(token);

    /// <summary>
    /// Digest over the complete pinned identity a policy row authorizes: the report contract, the
    /// stage matrix with its language capabilities, the observed tool/parser/runtime identities, the
    /// verifier mode and reason, the operation limits, the security validity window, and the pinned
    /// reference snapshot, model key and index key. Each JSON member is canonicalized before hashing,
    /// so key order and insignificant whitespace never change the identity while any real content or
    /// scalar change does. Recomputing this from the persisted row is what lets readiness prove the
    /// whole approved manifest rather than only the policy key and digest.
    /// </summary>
    public static string BuildRuntimeManifestDigest(
        string schemaName,
        string stageMatrixJson,
        string toolIdentitiesJson,
        string verifierMode,
        string verifierReason,
        string limitsJson,
        string securityValidityJson,
        string? modelKey,
        string? indexKey,
        string? referenceSnapshotId) =>
        CanonicalJson.DigestOf(CanonicalValue.Object(
            ("schema", CanonicalValue.Text("analysis-runtime-manifest-v1")),
            ("reportContract", CanonicalValue.Text(schemaName)),
            ("stageMatrix", CanonicalJsonMember(stageMatrixJson)),
            ("toolIdentities", CanonicalJsonMember(toolIdentitiesJson)),
            ("verifierMode", CanonicalValue.Text(verifierMode)),
            ("verifierReason", CanonicalValue.Text(verifierReason)),
            ("limits", CanonicalJsonMember(limitsJson)),
            ("securityValidity", CanonicalJsonMember(securityValidityJson)),
            ("referenceSnapshotId", NullableText(referenceSnapshotId)),
            ("modelKey", NullableText(modelKey)),
            ("indexKey", NullableText(indexKey))));

    /// <summary>True only for a canonical lowercase 64-character SHA-256 hex string.</summary>
    public static bool IsLowercaseSha256Hex(string? value) =>
        value is { Length: 64 } && !value.Any(char.IsAsciiLetterUpper) && value.All(IsLowercaseHexDigit);

    private static bool IsLowercaseHexDigit(char c) =>
        c is >= '0' and <= '9' or >= 'a' and <= 'f';

    /// <summary>Canonical JSON text of a stored member, so equivalent manifests digest identically.</summary>
    private static CanonicalValue CanonicalJsonMember(string json)
    {
        using var document = JsonDocument.Parse(json);
        return CanonicalValue.Text(CanonicalJson.CanonicalizeToString(document.RootElement));
    }

    private static CanonicalValue NullableText(string? value) =>
        value is null ? CanonicalValue.Null() : CanonicalValue.Text(value);

    private static CanonicalValue NullableTimestamp(DateTimeOffset? value) =>
        value is null ? CanonicalValue.Null() : CanonicalValue.Text(Timestamp(value.Value));

    private static string Timestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
}

/// <summary>Seller-declared license entry as sent to the analysis service.</summary>
internal sealed record DeclaredLicenseProjection(string ScopePath, string Spdx);

/// <summary>Admitted reference snapshot identity pinned by the analysis policy.</summary>
internal sealed record ReferenceProjection(string SnapshotId, string ModelKey, string IndexKey);
