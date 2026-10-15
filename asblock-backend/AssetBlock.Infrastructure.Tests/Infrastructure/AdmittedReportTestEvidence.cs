using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Infrastructure.Tests.Infrastructure;

/// <summary>
/// Builds the complete, coherent evidence chain a trusted publication test needs: archive and
/// malware security evidence for the exact content hash, the completed analysis run that consumed
/// it, the finalized report header bound to that run, and the server admission that binds the run's
/// input digest. Fixtures must never shortcut this chain with a bare boolean header.
/// </summary>
internal static class AdmittedReportTestEvidence
{
    public sealed record Chain(
        Guid PolicyVersionId,
        Guid ArchiveEvidenceId,
        Guid MalwareEvidenceId,
        Guid RunId,
        string InputDigest,
        string PolicyDigest,
        string PythonReportId,
        string ReportDigest);

    public static Chain BuildChain(string? overrideInputDigest = null)
    {
        return new Chain(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            overrideInputDigest ?? NewHex64(),
            NewHex64(),
            NewHex64(),
            NewHex64());
    }

    /// <summary>The policy row the run is admitted against; its digest is the report policy version.</summary>
    public static CodeAnalysisPolicyVersion Policy(Chain chain)
    {
        return new CodeAnalysisPolicyVersion
        {
            Id = chain.PolicyVersionId,
            PolicyKey = "code-analysis-policy-v1",
            PolicyDigest = chain.PolicyDigest,
            SchemaName = "code-analysis-v1",
            StageMatrixJson = "{}",
            VerifierMode = "PENDING_RESEARCH_EVALUATION",
            VerifierReason = "Retrieval quality research evaluation has not been reviewed.",
            ToolIdentitiesJson = "{}",
            LimitsJson = "{}",
            SecurityValidityJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static SecurityEvidenceRecord ArchiveEvidence(AssetVersion version, Chain chain)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new SecurityEvidenceRecord
        {
            Id = chain.ArchiveEvidenceId,
            AssetId = version.AssetId,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            EvidenceKind = SecurityEvidenceKind.ARCHIVE_VALIDATION,
            Generation = 1,
            DefinitionVersion = 1,
            Outcome = SecurityEvidenceOutcome.VALID,
            SecurityPolicyVersion = "policy-v1",
            ImplementationIdentity = "archive-inspector",
            IdentityAvailable = false,
            ScannedByteCount = version.ContentLength,
            ScannedEntryCount = 1,
            ScopeComplete = true,
            CompletedAt = now,
            EvidenceDigest = NewHex64(),
            CreatedAt = now
        };
    }

    public static SecurityEvidenceRecord MalwareEvidence(AssetVersion version, Chain chain)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new SecurityEvidenceRecord
        {
            Id = chain.MalwareEvidenceId,
            AssetId = version.AssetId,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            EvidenceKind = SecurityEvidenceKind.MALWARE_SCAN,
            Generation = 1,
            DefinitionVersion = 1,
            Outcome = SecurityEvidenceOutcome.CLEAN,
            SecurityPolicyVersion = "policy-v1",
            EngineIdentity = "clamav",
            EngineVersion = "1.0.3",
            SignatureIdentity = "clamav 1.0.3/25425",
            SignatureObservedAt = now,
            SignatureValidUntil = now.AddDays(2),
            IdentityAvailable = true,
            ScannedByteCount = version.ContentLength,
            ScopeComplete = true,
            CompletedAt = now,
            EvidenceDigest = NewHex64(),
            CreatedAt = now
        };
    }

    public static CodeAnalysisRun CompletedRun(AssetVersion version, Chain chain, CodeAnalysisRunState state = CodeAnalysisRunState.COMPLETED)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new CodeAnalysisRun
        {
            Id = chain.RunId,
            AssetId = version.AssetId,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            PolicyVersionId = chain.PolicyVersionId,
            PolicyDigest = chain.PolicyDigest,
            DeclarationDigest = NewHex64(),
            MaterialDigest = NewHex64(),
            InputDigest = chain.InputDigest,
            SelectionGeneration = 1,
            ArchiveEvidenceId = chain.ArchiveEvidenceId,
            MalwareEvidenceId = chain.MalwareEvidenceId,
            SecurityGeneration = 1,
            ServiceKey = "code-analysis",
            State = state,
            CreatedAt = now,
            CompletedAt = state == CodeAnalysisRunState.COMPLETED ? now : null
        };
    }

    /// <summary>The policy key a report header must carry; the digest lives on the run.</summary>
    public static string PolicyVersionFor(Chain chain) => "code-analysis-policy-v1";

    public static CodeAnalysisReportHeader Header(AssetVersion version, Chain chain, bool omitRunBinding = false)
    {
        return new CodeAnalysisReportHeader
        {
            Id = Guid.NewGuid(),
            AssetId = version.AssetId,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            PolicyVersion = "code-analysis-policy-v1",
            InputRevision = 1,
            Purpose = CodeAnalysisReportPurpose.PRODUCTION,
            CanAuthorizePublication = true,
            IsFinalized = true,
            FinalizedAt = DateTimeOffset.UtcNow,
            ReportSchemaVersion = 1,
            AnalysisRunId = omitRunBinding ? null : chain.RunId,
            PythonReportId = chain.PythonReportId,
            ReportDigest = chain.ReportDigest,
            SelectionGeneration = 1,
            CoverageLevel = CodeAnalysisCoverageLevel.FULL,
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static CodeAnalysisReportAdmission Admission(
        CodeAnalysisReportHeader report, Chain chain, bool canAuthorizePublication)
    {
        return new CodeAnalysisReportAdmission
        {
            Id = Guid.NewGuid(),
            ReportId = report.Id,
            AdmissionKind = CodeAnalysisAdmissionKind.SERVER_VALIDATED,
            AdmittedBy = "admitted-report-test-evidence",
            AdmittedAt = DateTimeOffset.UtcNow,
            ReportDigest = chain.ReportDigest,
            PythonReportId = chain.PythonReportId,
            InputDigest = chain.InputDigest,
            ServiceKey = "code-analysis",
            ReservationSlotNumber = 0,
            ReservationGeneration = 1,
            ReservationLeaseTokenDigest = NewHex64(),
            CanAuthorizePublication = canAuthorizePublication,
            PublicationAuthorityReason = canAuthorizePublication
                ? "test evidence granting publication authority"
                : "publication authority is unavailable pending research evaluation",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static string NewHex64() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
