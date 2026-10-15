using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// The exact persisted proof an analysis admission is allowed to observe. Initial upload,
/// recovered scheduling and report admission all share this contract, so none of them can
/// schedule or admit a proof the others would reject: the exact evidence identities, their
/// hash/bytes/scope/outcome, the scanner identity and its expiry, the producing job that
/// actually observed them under the approved policy, and the security selection that is still
/// current for the version.
/// </summary>
internal static class CurrentSecurityProof
{
    public static async Task<SecurityProofVerdict> Verify(
        ApplicationDbContext dbContext,
        SecurityProofExpectation expectation,
        long versionContentLength,
        DateTimeOffset dbNow,
        CancellationToken cancellationToken = default)
    {
        // 1. The advertised proof must still be the selected security generation for this version.
        //    A recheck start moves the selection immediately, so a superseded intent is detected
        //    before any of its evidence is inspected. The selected row also carries the exact
        //    policy and definition identity the proof has to reproduce.
        List<SelectionRow> selections = await dbContext.Database.SqlQueryRaw<SelectionRow>(
            """
            SELECT "Id", "SecurityGeneration" AS "Generation", "State"::text AS "State", "CompletedGeneration",
                   "SecurityPolicyVersion", "DefinitionVersion"
            FROM security_rechecks
            WHERE "AssetVersionId" = {0} AND "SelectedGeneration" IS NOT NULL
            """, expectation.AssetVersionId).ToListAsync(cancellationToken).ConfigureAwait(false);

        string expectedPolicyVersion;
        int? expectedDefinitionVersion;

        if (expectation.SecurityRecheckId is null)
        {
            if (expectation.SecurityGeneration != 1)
            {
                return SecurityProofVerdict.Rejected(
                    AnalysisEnqueueRejection.PROOF_SUPERSEDED, "Unscoped proof must be generation 1.");
            }

            if (selections.Count > 0)
            {
                return SecurityProofVerdict.Rejected(
                    AnalysisEnqueueRejection.PROOF_SUPERSEDED,
                    "A security recheck superseded the initial proof selection.");
            }

            // Initial upload proof is admitted only under the one security policy this deployment
            // ever scans with, and its producing scanner job has to carry that same policy in its
            // own payload. Definition identity is bound to the producing job instead, because no
            // recheck row pins it for an initial generation.
            expectedPolicyVersion = AssetProcessingDefaults.MALWARE_POLICY_VERSION;
            expectedDefinitionVersion = null;
        }
        else
        {
            if (selections.Count != 1
                || selections[0].Id != expectation.SecurityRecheckId
                || selections[0].Generation != expectation.SecurityGeneration)
            {
                return SecurityProofVerdict.Rejected(
                    AnalysisEnqueueRejection.PROOF_SUPERSEDED,
                    "The proof belongs to a security generation that is no longer selected.");
            }

            if (!string.Equals(selections[0].State, nameof(SecurityRecheckState.SUCCEEDED), StringComparison.Ordinal)
                || selections[0].CompletedGeneration != expectation.SecurityGeneration)
            {
                return SecurityProofVerdict.Rejected(
                    AnalysisEnqueueRejection.PROOF_INVALID,
                    "The selected security generation has no completed proof.");
            }

            // The approved recheck identity is the authority for the policy and definition the
            // proof must reproduce; an altered non-blank policy value is never self-justifying.
            expectedPolicyVersion = selections[0].SecurityPolicyVersion;
            expectedDefinitionVersion = selections[0].DefinitionVersion;
        }

        // 2. Both exact evidence records must exist for this version.
        List<SecurityEvidenceRecord> evidence = await dbContext.SecurityEvidenceRecords
            .AsNoTracking()
            .Where(e => e.Id == expectation.ArchiveEvidenceId || e.Id == expectation.MalwareEvidenceId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        SecurityEvidenceRecord? archive = evidence.FirstOrDefault(e => e.Id == expectation.ArchiveEvidenceId);
        if (archive is null)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The archive proof record is missing.");
        }

        SecurityProofVerdict archiveVerdict = await VerifyArchive(
                dbContext, expectation, versionContentLength, dbNow, archive,
                expectedPolicyVersion, expectedDefinitionVersion, expectation.CompletingJobId,
                cancellationToken)
            .ConfigureAwait(false);
        if (!archiveVerdict.Accepted)
        {
            return archiveVerdict;
        }

        SecurityEvidenceRecord? malware = evidence.FirstOrDefault(e => e.Id == expectation.MalwareEvidenceId);
        if (malware is null)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The malware proof record is missing.");
        }

        return await VerifyMalware(
                dbContext, expectation, versionContentLength, dbNow, malware,
                expectedPolicyVersion, expectedDefinitionVersion, expectation.CompletingJobId,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<SecurityProofVerdict> VerifyArchive(
        ApplicationDbContext dbContext,
        SecurityProofExpectation expectation,
        long versionContentLength,
        DateTimeOffset dbNow,
        SecurityEvidenceRecord archive,
        string expectedPolicyVersion,
        int? expectedDefinitionVersion,
        Guid? completingJobId,
        CancellationToken cancellationToken)
    {
        SecurityProofVerdict common = VerifyCommon(
            expectation, versionContentLength, dbNow, SecurityEvidenceKind.ARCHIVE_VALIDATION,
            SecurityEvidenceOutcome.VALID, archive, expectedPolicyVersion, expectedDefinitionVersion);
        if (!common.Accepted)
        {
            return common;
        }

        if (string.IsNullOrWhiteSpace(archive.ImplementationIdentity))
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The archive proof carries no observed implementation identity.");
        }

        return await VerifyJobProvenance(
                dbContext, archive, AssetProcessingJobType.ARCHIVE_INSPECTION, expectedPolicyVersion,
                completingJobId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<SecurityProofVerdict> VerifyMalware(
        ApplicationDbContext dbContext,
        SecurityProofExpectation expectation,
        long versionContentLength,
        DateTimeOffset dbNow,
        SecurityEvidenceRecord malware,
        string expectedPolicyVersion,
        int? expectedDefinitionVersion,
        Guid? completingJobId,
        CancellationToken cancellationToken)
    {
        SecurityProofVerdict common = VerifyCommon(
            expectation, versionContentLength, dbNow, SecurityEvidenceKind.MALWARE_SCAN,
            SecurityEvidenceOutcome.CLEAN, malware, expectedPolicyVersion, expectedDefinitionVersion);
        if (!common.Accepted)
        {
            return common;
        }

        // The observed scanner identity is mandatory: an unavailable or incomplete identity is
        // reported explicitly and never admitted, so no deployment option can schedule a proof
        // that no concrete scanner observation backs.
        if (!malware.IdentityAvailable)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The observed scanner identity is unavailable.");
        }

        if (string.IsNullOrWhiteSpace(malware.EngineIdentity)
            || string.IsNullOrWhiteSpace(malware.EngineVersion)
            || string.IsNullOrWhiteSpace(malware.SignatureIdentity))
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The observed scanner identity is incomplete.");
        }

        if (malware.SignatureObservedAt is not { } observedAt || malware.SignatureValidUntil is not { } validUntil)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The scanner signature validity window is unobserved.");
        }

        if (observedAt > dbNow)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The scanner signature observation lies in the future.");
        }

        if (observedAt > validUntil)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The scanner signature validity window is incoherent.");
        }

        if (validUntil <= dbNow)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The scanner signature has expired.");
        }

        return await VerifyJobProvenance(
                dbContext, malware, AssetProcessingJobType.MALWARE_SCAN, expectedPolicyVersion,
                completingJobId, cancellationToken)
            .ConfigureAwait(false);
    }

    private static SecurityProofVerdict VerifyCommon(
        SecurityProofExpectation expectation,
        long versionContentLength,
        DateTimeOffset dbNow,
        SecurityEvidenceKind requiredKind,
        SecurityEvidenceOutcome requiredOutcome,
        SecurityEvidenceRecord record,
        string expectedPolicyVersion,
        int? expectedDefinitionVersion)
    {
        if (record.AssetId != expectation.AssetId || record.AssetVersionId != expectation.AssetVersionId)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof does not belong to the advertised version.");
        }

        if (!string.Equals(record.ContentSha256, expectation.ContentSha256, StringComparison.Ordinal))
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof covers different bytes than advertised.");
        }

        if (record.EvidenceKind != requiredKind)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof is of the wrong evidence kind.");
        }

        if (record.SecurityRecheckId != expectation.SecurityRecheckId
            || record.Generation != expectation.SecurityGeneration)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof belongs to another security generation.");
        }

        if (record.Outcome != requiredOutcome)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof outcome is not admissible.");
        }

        if (!record.ScopeComplete || record.ScannedByteCount != versionContentLength)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof does not cover the full advertised byte scope.");
        }

        if (record.CompletedAt > dbNow)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof completion lies in the future.");
        }

        if (record.DefinitionVersion <= 0)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof carries no definition identity.");
        }

        if (expectedDefinitionVersion is { } pinnedDefinition && record.DefinitionVersion != pinnedDefinition)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID,
                "The proof carries a definition version the approved security selection did not pin.");
        }

        if (string.IsNullOrWhiteSpace(record.SecurityPolicyVersion))
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof carries no security policy identity.");
        }

        if (!string.Equals(record.SecurityPolicyVersion, expectedPolicyVersion, StringComparison.Ordinal))
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID,
                "The proof carries a security policy the deployment did not approve.");
        }

        return SecurityProofVerdict.Valid();
    }

    /// <summary>
    /// Server-written proof records the job that observed it, and an admitted proof always has
    /// one: the link is mandatory, resolves to exactly that job for this version, kind, definition
    /// and security scope, and the job has to be an observed successful execution. The single
    /// exception is a producing job this same transaction completes after verifying the proof, so
    /// the initial malware transition can still prove its own in-transaction work; every other
    /// admission only ever accepts an already succeeded producer.
    /// The scanner producer additionally has to carry the approved policy in its own payload, so
    /// a proof never outlives the configuration the scanner actually ran under.
    /// </summary>
    private static async Task<SecurityProofVerdict> VerifyJobProvenance(
        ApplicationDbContext dbContext,
        SecurityEvidenceRecord record,
        AssetProcessingJobType expectedType,
        string expectedPolicyVersion,
        Guid? completingJobId,
        CancellationToken cancellationToken)
    {
        if (record.JobId is not { } jobId)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof records no producing job.");
        }

        var recheckParameter = (object?)record.SecurityRecheckId ?? DBNull.Value;

        List<ProducingJobRow> jobs = await dbContext.Database.SqlQueryRaw<ProducingJobRow>(
            """
            SELECT j."Id", j."Status"::text AS "Status", j."Payload"->>'policyVersion' AS "PayloadPolicyVersion"
            FROM asset_processing_jobs AS j
            WHERE j."Id" = {0}
              AND j."AssetId" = {1}
              AND j."AssetVersionId" = {2}
              AND j."Type" = {3}
              AND j."DefinitionVersion" = {4}
              AND j."SecurityRecheckId" IS NOT DISTINCT FROM {5}
            LIMIT 1
            """,
            jobId, record.AssetId, record.AssetVersionId, expectedType.ToString(), record.DefinitionVersion,
            recheckParameter).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (jobs.Count != 1)
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The proof does not bind to its producing job.");
        }

        ProducingJobRow job = jobs[0];

        if (jobId != completingJobId
            && !string.Equals(job.Status, nameof(AssetProcessingJobStatus.SUCCEEDED), StringComparison.Ordinal))
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID, "The producing job never completed successfully.");
        }

        if (expectedType == AssetProcessingJobType.MALWARE_SCAN
            && !string.Equals(job.PayloadPolicyVersion, expectedPolicyVersion, StringComparison.Ordinal))
        {
            return SecurityProofVerdict.Rejected(
                AnalysisEnqueueRejection.PROOF_INVALID,
                "The producing scanner job did not run under the approved security policy.");
        }

        return SecurityProofVerdict.Valid();
    }

    private sealed class SelectionRow
    {
        public Guid Id { get; init; }
        public int Generation { get; init; }
        public string State { get; init; } = "";
        public int? CompletedGeneration { get; init; }
        public string SecurityPolicyVersion { get; init; } = "";
        public int DefinitionVersion { get; init; }
    }

    private sealed class ProducingJobRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = "";
        public string? PayloadPolicyVersion { get; init; }
    }
}

/// <summary>The exact immutable proof an analysis admission must observe.</summary>
internal sealed record SecurityProofExpectation(
    Guid AssetId,
    Guid AssetVersionId,
    string ContentSha256,
    Guid? SecurityRecheckId,
    Guid ArchiveEvidenceId,
    Guid MalwareEvidenceId,
    int SecurityGeneration,
    Guid? CompletingJobId = null);

/// <summary>Result of the shared current-proof contract.</summary>
internal sealed record SecurityProofVerdict(bool Accepted, AnalysisEnqueueRejection Rejection, string Reason)
{
    public static SecurityProofVerdict Valid() => new(true, AnalysisEnqueueRejection.NONE, "");

    public static SecurityProofVerdict Rejected(AnalysisEnqueueRejection rejection, string reason) =>
        new(false, rejection, reason);
}
