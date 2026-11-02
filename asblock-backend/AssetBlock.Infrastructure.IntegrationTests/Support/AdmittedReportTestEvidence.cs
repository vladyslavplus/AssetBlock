using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.IntegrationTests.Support;

/// <summary>
/// Builds the complete, coherent evidence chain a trusted publication test needs: the producing
/// archive and malware jobs, the security evidence for the exact content hash that they observed,
/// the completed analysis run that consumed it, the finalized report header bound to that run, and
/// the server admission that binds the run's input digest. Fixtures must never shortcut this chain
/// with a bare boolean header or with evidence no job ever produced.
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
        string ReportDigest,
        string ModelKey,
        string IndexKey);

    /// <summary>
    /// The frozen encoder identity the approved manifest pins. The model key is a 64-character
    /// lowercase hex digest because the persisted column is fixed-width: a shorter value would come
    /// back space-padded and would no longer reproduce the approved manifest digest.
    /// </summary>
    public const string MODEL_KEY = "5f0dc256eb0904af496d427451da5d7f90261676a1b2c3d4e5f60718293a4b5c";

    /// <summary>A stable pinned reference index key, so the approved manifest identity is stable.</summary>
    public const string INDEX_KEY = "0f1e2d3c4b5a69788796a5b4c3d2e1f00f1e2d3c4b5a69788796a5b4c3d2e1f0";

    /// <summary>The pinned reference snapshot the approved manifest requires.</summary>
    public const string REFERENCE_SNAPSHOT_ID = "snapshot-2026-10-01";

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
            NewHex64(),
            MODEL_KEY,
            INDEX_KEY);
    }

    /// <summary>
    /// The approved policy row the run is admitted against. It carries a complete runtime manifest
    /// that satisfies the same semantic contract the publisher enforces, so a test can never satisfy
    /// the readiness gate with an empty or partial manifest.
    /// </summary>
    public static CodeAnalysisPolicyVersion Policy(
        Chain chain,
        string? stageMatrixJson = null,
        string? toolIdentitiesJson = null,
        string? verifierReason = null,
        string? modelKey = null,
        string? indexKey = null,
        string? referenceSnapshotId = null) =>
        new()
        {
            Id = chain.PolicyVersionId,
            PolicyKey = ReviewAnalysisPolicy.KEY,
            PolicyDigest = chain.PolicyDigest,
            SchemaName = "code-analysis-v1",
            StageMatrixJson = stageMatrixJson ?? StageMatrixJson(),
            VerifierMode = ReviewAnalysisPolicy.VERIFIER_MODE,
            VerifierReason = verifierReason ?? ReviewAnalysisPolicy.VERIFIER_REASON,
            ToolIdentitiesJson = toolIdentitiesJson ?? ToolIdentitiesJson(),
            LimitsJson = LimitsJson(),
            SecurityValidityJson = SecurityValidityJson(),
            ModelKey = modelKey ?? chain.ModelKey,
            IndexKey = indexKey ?? chain.IndexKey,
            ReferenceSnapshotId = referenceSnapshotId ?? REFERENCE_SNAPSHOT_ID,
            CreatedAt = DateTimeOffset.UtcNow
        };

    /// <summary>The complete runtime-manifest digest the persisted approved row authorizes.</summary>
    public static string RuntimeManifestDigest { get; } =
        ProductDigests.BuildRuntimeManifestDigest(
            "code-analysis-v1",
            StageMatrixJson(),
            ToolIdentitiesJson(),
            "DISABLED_BY_EVALUATION",
            ReviewAnalysisPolicy.VERIFIER_REASON,
            LimitsJson(),
            SecurityValidityJson(),
            MODEL_KEY,
            INDEX_KEY,
            REFERENCE_SNAPSHOT_ID);

    /// <summary>The frozen eight-stage matrix with the language capabilities the service parses.</summary>
    public static string StageMatrixJson() =>
        """
        {
          "stages": [
            {"stage": "INVENTORY", "applicability": "MANDATORY"},
            {"stage": "EXTRACTION", "applicability": "MANDATORY"},
            {"stage": "TOKEN", "applicability": "MANDATORY"},
            {"stage": "SEMANTIC", "applicability": "MANDATORY"},
            {"stage": "LICENSE", "applicability": "MANDATORY"},
            {"stage": "UNION", "applicability": "MANDATORY"},
            {"stage": "VERIFIER", "applicability": "MANDATORY"},
            {"stage": "REPORT", "applicability": "MANDATORY"}
          ],
          "capabilities": [
            {"group": "javascript", "dialects": ["javascript"], "parser": true, "token": true},
            {"group": "typescript", "dialects": ["typescript", "tsx"], "parser": true, "token": true},
            {"group": "python", "dialects": ["python"], "parser": true, "token": true},
            {"group": "java", "dialects": ["java"], "parser": true, "token": true},
            {"group": "csharp", "dialects": ["csharp"], "parser": true, "token": true}
          ]
        }
        """;

    /// <summary>The frozen encoder identity retrieval must prove it actually used.</summary>
    public static string ToolIdentitiesJson() =>
        $$"""
        {
          "encoder": "unixcoder-base-nine",
          "checkpointSha256": "{{new string('a', 64)}}",
          "loadedStateSha256": "{{new string('d', 64)}}",
          "selectionSha256": "{{new string('c', 64)}}",
          "tokenizerHashes": {"tokenizer.json": "{{new string('b', 64)}}"},
          "framing": "encoder-only",
          "pooling": "padding-masked mean then L2",
          "mask": "padding attention mask",
          "preprocessing": "utf-8 strict, no execution",
          "chunking": "encoder-only-256-overlap32-max16-v1",
          "dimension": 768,
          "device": "cpu",
          "parserBuilds": {"csharp": "tree-sitter-c-sharp", "javascript": "tree-sitter-javascript"},
          "dolosVersion": "0.1.2",
          "scancodeVersion": "32.3.0",
          "serviceVersion": "1.0.0",
          "referenceRevision": "revision-1"
        }
        """;

    /// <summary>The immutable operation budget, mirroring the frozen policy budgets.</summary>
    public static string LimitsJson() =>
        """
        {
          "maxCompressedBytes": 104857600,
          "maxEntries": 2000,
          "maxExpandedBytes": 209715200,
          "maxSourceBytes": 2097152,
          "maxUnits": 10000,
          "maxWindows": 20000,
          "maxResponseBytes": 8388608,
          "maxFindings": 200,
          "tokenK": 23,
          "tokenWindow": 17,
          "tokenTop": 20,
          "semanticTop": 20,
          "unionBudget": 40
        }
        """;

    /// <summary>The security validity window admitted proof must satisfy.</summary>
    public static string SecurityValidityJson() =>
        """
        {
          "contentSha256": "exact-advertised-hash",
          "scopeComplete": "mandatory",
          "scannerIdentity": "engine-version-signature-observed",
          "signatureWindowDays": 2
        }
        """;

    public static SecurityEvidenceRecord ArchiveEvidence(
        AssetVersion version,
        Chain chain,
        Guid? securityRecheckId = null,
        int generation = 1,
        string? securityPolicyVersion = null,
        int definitionVersion = 1,
        Guid? jobId = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new SecurityEvidenceRecord
        {
            Id = chain.ArchiveEvidenceId,
            AssetId = version.AssetId,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            SecurityRecheckId = securityRecheckId,
            JobId = jobId,
            EvidenceKind = SecurityEvidenceKind.ARCHIVE_VALIDATION,
            Generation = generation,
            DefinitionVersion = definitionVersion,
            Outcome = SecurityEvidenceOutcome.VALID,
            SecurityPolicyVersion = securityPolicyVersion ?? AssetProcessingDefaults.MALWARE_POLICY_VERSION,
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

    public static SecurityEvidenceRecord MalwareEvidence(
        AssetVersion version,
        Chain chain,
        Guid? securityRecheckId = null,
        int generation = 1,
        string? securityPolicyVersion = null,
        int definitionVersion = 1,
        Guid? jobId = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new SecurityEvidenceRecord
        {
            Id = chain.MalwareEvidenceId,
            AssetId = version.AssetId,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            SecurityRecheckId = securityRecheckId,
            JobId = jobId,
            EvidenceKind = SecurityEvidenceKind.MALWARE_SCAN,
            Generation = generation,
            DefinitionVersion = definitionVersion,
            Outcome = SecurityEvidenceOutcome.CLEAN,
            SecurityPolicyVersion = securityPolicyVersion ?? AssetProcessingDefaults.MALWARE_POLICY_VERSION,
            EngineIdentity = "clamav",
            EngineVersion = "1.0.3",
            SignatureIdentity = "clamav 1.0.3/25425",

            // Observed strictly before the row is written, so host/database clock alignment can
            // never turn a coherent observation into an insufficient scanner identity.
            SignatureObservedAt = now.AddMinutes(-1),
            SignatureValidUntil = now.AddDays(2),
            IdentityAvailable = true,
            ScannedByteCount = version.ContentLength,
            ScopeComplete = true,
            CompletedAt = now,
            EvidenceDigest = NewHex64(),
            CreatedAt = now
        };
    }

    /// <summary>
    /// Adds the producing archive/malware jobs and the evidence they observed to the context,
    /// exactly as the initial-upload and recheck writers persist them, so the current-proof
    /// contract sees coherent proof instead of orphan evidence rows: every job is bound to its
    /// evidence, the scanner job carries the approved security policy in its own payload, and the
    /// producer status is the one a test needs to exercise. The caller's own save persists them.
    /// </summary>
    public static (SecurityEvidenceRecord Archive, SecurityEvidenceRecord Malware) SeedObservedProof(
        ApplicationDbContext db,
        AssetVersion version,
        Chain chain,
        Guid? securityRecheckId = null,
        int generation = 1,
        string? securityPolicyVersion = null,
        int definitionVersion = 1,
        AssetProcessingJobStatus producerStatus = AssetProcessingJobStatus.SUCCEEDED)
    {
        var policyVersion = securityPolicyVersion ?? AssetProcessingDefaults.MALWARE_POLICY_VERSION;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        AssetProcessingJob archiveJob = BuildProducerJob(
            version, AssetProcessingJobType.ARCHIVE_INSPECTION, securityRecheckId, definitionVersion,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.ARCHIVE_INSPECTION, new ArchiveInspectionPayload()),
            producerStatus, now);

        AssetProcessingJob malwareJob = BuildProducerJob(
            version, AssetProcessingJobType.MALWARE_SCAN, securityRecheckId, definitionVersion,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.MALWARE_SCAN, new MalwareScanPayload(policyVersion)),
            producerStatus, now);

        SecurityEvidenceRecord archive = ArchiveEvidence(
            version, chain, securityRecheckId, generation, policyVersion, definitionVersion, archiveJob.Id);
        SecurityEvidenceRecord malware = MalwareEvidence(
            version, chain, securityRecheckId, generation, policyVersion, definitionVersion, malwareJob.Id);

        db.AssetProcessingJobs.AddRange(archiveJob, malwareJob);
        db.SecurityEvidenceRecords.AddRange(archive, malware);
        return (archive, malware);
    }

    /// <summary>Same coherent proof as <see cref="SeedObservedProof"/>, persisted by this call.</summary>
    public static async Task<(SecurityEvidenceRecord Archive, SecurityEvidenceRecord Malware)> SeedObservedProofAsync(
        ApplicationDbContext db,
        AssetVersion version,
        Chain chain,
        Guid? securityRecheckId = null,
        int generation = 1,
        string? securityPolicyVersion = null,
        int definitionVersion = 1,
        AssetProcessingJobStatus producerStatus = AssetProcessingJobStatus.SUCCEEDED)
    {
        (SecurityEvidenceRecord archive, SecurityEvidenceRecord malware) = SeedObservedProof(
            db, version, chain, securityRecheckId, generation, securityPolicyVersion, definitionVersion, producerStatus);

        await db.SaveChangesAsync();
        return (archive, malware);
    }

    private static AssetProcessingJob BuildProducerJob(
        AssetVersion version,
        AssetProcessingJobType type,
        Guid? securityRecheckId,
        int definitionVersion,
        string payload,
        AssetProcessingJobStatus status,
        DateTimeOffset now)
    {
        var terminal = status is AssetProcessingJobStatus.SUCCEEDED
            or AssetProcessingJobStatus.FAILED
            or AssetProcessingJobStatus.CANCELLED;

        return new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = version.AssetId,
            AssetVersionId = version.Id,
            Type = type,
            DefinitionVersion = definitionVersion,
            Status = status,
            Stage = status.ToString(),
            AttemptCount = 0,
            MaxAttempts = 3,
            AvailableAt = now,
            StartedAt = status is AssetProcessingJobStatus.QUEUED ? null : now,
            CompletedAt = terminal ? now : null,
            Payload = payload,
            SecurityRecheckId = securityRecheckId,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    public static CodeAnalysisRun CompletedRun(AssetVersion version, Chain chain, CodeAnalysisRunState state = CodeAnalysisRunState.COMPLETED)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var terminal = state is CodeAnalysisRunState.COMPLETED or CodeAnalysisRunState.FAILED or CodeAnalysisRunState.CANCELLED;
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
            CompletedAt = terminal ? now : null
        };
    }

    /// <summary>The policy key a report header must carry; the digest lives on the run.</summary>
    public static string PolicyVersionFor(Chain chain) => "code-analysis-policy-v1";

    public static CodeAnalysisReportHeader Header(
        AssetVersion version, Chain chain, bool omitRunBinding = false, bool contentHashMismatch = false)
    {
        return new CodeAnalysisReportHeader
        {
            Id = Guid.NewGuid(),
            AssetId = version.AssetId,
            AssetVersionId = version.Id,
            ContentSha256 = contentHashMismatch ? new string('f', 64) : version.ContentSha256,
            PolicyVersion = "code-analysis-policy-v1",
            InputRevision = 1,
            Purpose = CodeAnalysisReportPurpose.PRODUCTION,
            ReviewEligible = true,
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
        CodeAnalysisReportHeader report, Chain chain, bool reviewEligible)
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
            ReviewEligible = reviewEligible,
            ReviewEligibilityReason = reviewEligible
                ? "test evidence granting review eligibility"
                : "test evidence denying review eligibility",
            CreatedAt = DateTimeOffset.UtcNow
        };
    }

    public static string NewHex64() =>
        Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
}
