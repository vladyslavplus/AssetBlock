using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// Reads the live reservation behind a claimed analysis job. The run row carries the persisted
/// transport binding, but every value used for the request is re-joined against the current slot,
/// pinned policy, and declaration so a stale or hijacked claim can never produce a request the
/// service would accept.
/// </summary>
internal sealed class AnalysisReservationReader(
    ApplicationDbContext dbContext,
    ILogger<AnalysisReservationReader> logger)
    : IAnalysisReservationReader
{
    public async Task<AnalysisReservationBinding?> Read(
        AssetProcessingJobContext<CodeAnalysisPayload> context,
        CancellationToken cancellationToken = default)
    {
        CodeAnalysisPayload payload = context.Payload;

        DateTimeOffset dbNow = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset>(/*-sql*/ """SELECT clock_timestamp() AS "Value" """)
            .FirstAsync(cancellationToken)
            .ConfigureAwait(false);

        AssetProcessingJob? job = await dbContext.AssetProcessingJobs
            .AsNoTracking()
            .FirstOrDefaultAsync(j => j.Id == context.JobId, cancellationToken)
            .ConfigureAwait(false);

        // Exact live job lease: type, status, asset binding, lease token and DB-clock expiry.
        if (job is null
            || job.Type != AssetProcessingJobType.CODE_ANALYSIS
            || job.Status != AssetProcessingJobStatus.RUNNING
            || job.AssetId != context.AssetId
            || job.AssetVersionId != context.AssetVersionId
            || job.LeaseToken != context.LeaseToken
            || job.LeaseExpiresAt is null
            || job.LeaseExpiresAt <= dbNow
            || job.AnalysisRunId != payload.AnalysisRunId
            || string.IsNullOrEmpty(job.TransportLeaseToken)
            || job.TransportLeaseToken.Length != 64)
        {
            logger.LogWarning("Job {JobId} has no live lease for this claim.", context.JobId);
            return null;
        }

        CodeAnalysisRun? run = await dbContext.CodeAnalysisRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == payload.AnalysisRunId, cancellationToken)
            .ConfigureAwait(false);

        if (run is null
            || run.AssetId != context.AssetId
            || run.AssetVersionId != context.AssetVersionId
            || run.JobId != context.JobId
            || run.State != CodeAnalysisRunState.RUNNING)
        {
            logger.LogWarning("Job {JobId} is not bound to a live analysis run.", context.JobId);
            return null;
        }

        // Full persisted payload tuple must match the claim: input, content, policy and
        // exact security evidence identity. A changed payload never starts an HTTP request.
        if (!string.Equals(run.InputDigest, payload.InputDigest, StringComparison.Ordinal)
            || !string.Equals(run.ContentSha256, payload.ContentSha256, StringComparison.Ordinal)
            || !string.Equals(run.PolicyDigest, payload.PolicyDigest, StringComparison.Ordinal)
            || run.ArchiveEvidenceId != payload.ArchiveEvidenceId
            || run.MalwareEvidenceId != payload.MalwareEvidenceId
            || run.SecurityGeneration != payload.SecurityGeneration
            || !string.Equals(run.TransportLeaseToken, job.TransportLeaseToken, StringComparison.Ordinal))
        {
            logger.LogWarning("Analysis run {RunId} no longer matches the claimed payload.", run.Id);
            return null;
        }

        // A run must already carry its transport identity before it may be executed.
        if (string.IsNullOrEmpty(run.TransportLeaseToken)
            || run.ReservationGeneration is not { } generation
            || generation < 1
            || run.HardDeadlineUtc is not { } hardDeadline)
        {
            logger.LogWarning("Analysis run {RunId} has no complete transport binding.", run.Id);
            return null;
        }

        AnalysisCapacitySlot? slot = await dbContext.AnalysisCapacitySlots
            .AsNoTracking()
            .FirstOrDefaultAsync(
                s => s.ServiceKey == run.ServiceKey
                    && s.ReservedRunId == run.Id
                    && s.ReservedJobId == run.JobId,
                cancellationToken)
            .ConfigureAwait(false);

        // The slot must still be held under the exact same transport identity as the run row,
        // with an equal hard deadline that has not expired on the database clock.
        if (slot is null
            || slot.State != AnalysisCapacitySlotState.RESERVED
            || !string.Equals(slot.LeaseToken, run.TransportLeaseToken, StringComparison.Ordinal)
            || slot.ReservationGeneration != generation
            || slot.HardDeadlineUtc is null
            || slot.HardDeadlineUtc != hardDeadline
            || slot.HardDeadlineUtc <= dbNow)
        {
            logger.LogWarning("Analysis run {RunId} no longer holds its capacity slot.", run.Id);
            return null;
        }

        Asset? asset = await dbContext.Assets
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == context.AssetId, cancellationToken)
            .ConfigureAwait(false);

        AssetVersion? version = await dbContext.AssetVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == context.AssetVersionId && v.AssetId == context.AssetId, cancellationToken)
            .ConfigureAwait(false);

        // Undeleted, non-quarantined target with a live selection generation and matching
        // content hash. Delete, quarantine, edit or a newer selection fences the old claim.
        if (asset is null
            || asset.DeletedAt is not null
            || version is null
            || version.SafetyRestriction != AssetVersionSafetyRestriction.NONE
            || !string.Equals(version.ContentSha256, run.ContentSha256, StringComparison.Ordinal)
            || !string.Equals(version.ContentSha256, payload.ContentSha256, StringComparison.Ordinal)
            || version.AnalysisSelectionGeneration != run.SelectionGeneration)
        {
            logger.LogWarning("Analysis target for job {JobId} is no longer executable.", context.JobId);
            return null;
        }

        // Revision and workspace scope: the run's declaration/material revisions must still be
        // the content the input digest was built over, and the workspace scope must match.
        if (!await RevisionScopeMatches(run, cancellationToken).ConfigureAwait(false))
        {
            logger.LogWarning("Analysis run {RunId} lost its revision scope.", run.Id);
            return null;
        }

        CodeAnalysisPolicyVersion? policy = await dbContext.CodeAnalysisPolicyVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == run.PolicyVersionId, cancellationToken)
            .ConfigureAwait(false);

        // The pinned policy must still hash to the digest the run was admitted under.
        if (policy is null
            || !string.Equals(policy.PolicyDigest, run.PolicyDigest, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(policy.PolicyKey))
        {
            logger.LogWarning("Analysis run {RunId} lost its pinned policy identity.", run.Id);
            return null;
        }

        IReadOnlyList<CodeAnalysisDeclaredLicense> declaredLicenses = await ReadLicenses(
            run, cancellationToken).ConfigureAwait(false);
        CodeAnalysisReuseRequest? reuse = await ReadReuse(
            payload, run, policy, cancellationToken).ConfigureAwait(false);

        return new AnalysisReservationBinding(
            slot.ServiceKey,
            slot.SlotNumber,
            generation,
            run.TransportLeaseToken,
            hardDeadline,
            policy.PolicyKey,
            run.DeclarationDigest,
            run.MaterialDigest,
            declaredLicenses,
            reuse);
    }

    private async Task<CodeAnalysisReuseRequest?> ReadReuse(
        CodeAnalysisPayload payload,
        CodeAnalysisRun run,
        CodeAnalysisPolicyVersion policy,
        CancellationToken cancellationToken)
    {
        if (payload.ReuseSourceRunId is not { } sourceRunId)
        {
            return payload.ReuseSourceInputDigest is null && payload.ReuseSourceMaterialDigest is null
                ? null
                : throw new InvalidOperationException("A partial code-analysis reuse source binding is invalid.");
        }

        if (!ProductDigests.IsLowercaseSha256Hex(payload.ReuseSourceInputDigest ?? "")
            || !ProductDigests.IsLowercaseSha256Hex(payload.ReuseSourceMaterialDigest ?? ""))
        {
            throw new InvalidOperationException("The persisted declaration-only reuse digest binding is invalid.");
        }

        CodeAnalysisRun? source = await dbContext.CodeAnalysisRuns.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == sourceRunId, cancellationToken).ConfigureAwait(false);
        AssetVersion? currentVersion = source is null
            ? null
            : await dbContext.AssetVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.Id == run.AssetVersionId && v.AssetId == run.AssetId, cancellationToken)
                .ConfigureAwait(false);

        if (source is null
            || currentVersion is null
            || source.State != CodeAnalysisRunState.COMPLETED
            || source.Disposition != CodeAnalysisReportDisposition.SELECTED
            || source.SelectionGeneration >= run.SelectionGeneration
            || currentVersion.AnalysisSelectionGeneration != run.SelectionGeneration
            || source.AssetId != run.AssetId
            || source.AssetVersionId != run.AssetVersionId
            || !string.Equals(source.InputDigest, payload.ReuseSourceInputDigest, StringComparison.Ordinal)
            || !string.Equals(source.MaterialDigest, payload.ReuseSourceMaterialDigest, StringComparison.Ordinal)
            || string.Equals(source.DeclarationDigest, run.DeclarationDigest, StringComparison.Ordinal)
            || !string.Equals(source.ContentSha256, run.ContentSha256, StringComparison.Ordinal)
            || !string.Equals(source.PolicyDigest, run.PolicyDigest, StringComparison.Ordinal)
            || !string.Equals(source.MaterialDigest, run.MaterialDigest, StringComparison.Ordinal)
            || source.SecurityGeneration != run.SecurityGeneration
            || source.ArchiveEvidenceId != run.ArchiveEvidenceId
            || source.MalwareEvidenceId != run.MalwareEvidenceId)
        {
            logger.LogWarning("Analysis run {RunId} has no matching declaration-only reuse source.", run.Id);
            throw new InvalidOperationException("The persisted declaration-only reuse source is stale or mismatched.");
        }

        var sourceReports = await (
            from header in dbContext.CodeAnalysisReportHeaders.AsNoTracking()
            join admission in dbContext.CodeAnalysisReportAdmissions.AsNoTracking() on header.Id equals admission.ReportId
            where header.AnalysisRunId == source.Id
                && header.IsFinalized
                && header.Disposition == CodeAnalysisReportDisposition.SELECTED
                && header.SelectionGeneration == source.SelectionGeneration
                && admission.AdmissionKind == CodeAnalysisAdmissionKind.SERVER_VALIDATED
                && !admission.CanAuthorizePublication
                && admission.ReportDigest == header.ReportDigest
            select new { header.Id, header.ReportDigest }).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (sourceReports.Count != 1)
        {
            logger.LogWarning("Reuse source run {SourceRunId} has no unique admitted report.", source.Id);
            throw new InvalidOperationException("The declaration-only reuse source report is no longer uniquely selected and admitted.");
        }

        var sourceReport = sourceReports[0];
        List<CodeAnalysisReuseArtifact> artifacts = await dbContext.CodeAnalysisReuseArtifacts.AsNoTracking()
            .Where(a => a.SourceRunId == source.Id && a.SourceReportId == sourceReport.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        string[] expectedStages = ["EXTRACTION", "TOKEN", "SEMANTIC"];
        if (artifacts.Count != expectedStages.Length
            || artifacts.Select(a => a.Stage).Distinct(StringComparer.Ordinal).Count() != expectedStages.Length
            || !expectedStages.All(stage => artifacts.Any(a => string.Equals(a.Stage, stage, StringComparison.Ordinal))))
        {
            logger.LogWarning("Reuse source report {ReportId} lacks the complete extraction/token/vector artifact set.", sourceReport.Id);
            throw new InvalidOperationException("The declaration-only reuse artifact set is incomplete or ambiguous.");
        }

        var output = new List<CodeAnalysisReuseStageArtifact>(artifacts.Count);
        foreach (var expectedStage in expectedStages)
        {
            CodeAnalysisReuseArtifact artifact = artifacts.Single(a => a.Stage == expectedStage);
            CodeAnalysisReportStage? sourceStage = await dbContext.CodeAnalysisReportStages.AsNoTracking()
                .SingleOrDefaultAsync(s => s.Id == artifact.SourceStageId
                    && s.ReportId == sourceReport.Id
                    && s.Stage == artifact.Stage
                    && s.Outcome == CodeAnalysisStageOutcome.SUCCEEDED,
                    cancellationToken).ConfigureAwait(false);
            if (sourceStage is null
                || !string.Equals(sourceStage.OutputSha256, artifact.ArtifactDigest, StringComparison.Ordinal)
                || !TryVerifyArtifactBinding(artifact, source, policy))
            {
                logger.LogWarning("Reuse artifact for run {SourceRunId} stage {Stage} failed provenance checks.", source.Id, artifact.Stage);
                throw new InvalidOperationException("A declaration-only reuse artifact failed source-stage or digest verification.");
            }

            output.Add(new CodeAnalysisReuseStageArtifact(
                artifact.Stage,
                sourceReport.Id.ToString("D"),
                sourceStage.Id.ToString("D"),
                artifact.ArtifactDigest,
                artifact.ArtifactJson));
        }

        return new CodeAnalysisReuseRequest(
            source.Id.ToString("D"),
            source.InputDigest,
            source.DeclarationDigest,
            source.MaterialDigest,
            output);
    }

    internal static bool TryVerifyArtifactBinding(
        CodeAnalysisReuseArtifact artifact,
        CodeAnalysisRun source,
        CodeAnalysisPolicyVersion policy)
    {
        try
        {
            using var document = JsonDocument.Parse(artifact.ArtifactJson);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !HasExactJsonProperties(root, "binding", "data")
                || !root.TryGetProperty("binding", out JsonElement binding)
                || binding.ValueKind != JsonValueKind.Object
                || !HasExactJsonProperties(binding, "sourceRunId", "sourceInputDigest", "contentSha256", "policyDigest",
                    "materialDigest", "declarationDigest", "securityGeneration", "archiveEvidenceId", "malwareEvidenceId",
                    "stage", "snapshotId", "snapshotRevision", "modelKey", "indexKey", "modelIdentitySha256")
                || !root.TryGetProperty("data", out JsonElement data)
                || !CodeAnalysisReportValidator.ValidateReuseData(artifact.Stage, data)
                || !binding.TryGetProperty("sourceRunId", out JsonElement sourceRunId)
                || sourceRunId.GetString() != source.Id.ToString("D")
                || !binding.TryGetProperty("sourceInputDigest", out JsonElement inputDigest)
                || inputDigest.GetString() != source.InputDigest
                || !binding.TryGetProperty("contentSha256", out JsonElement contentSha256)
                || contentSha256.GetString() != source.ContentSha256
                || !binding.TryGetProperty("policyDigest", out JsonElement policyDigest)
                || policyDigest.GetString() != source.PolicyDigest
                || !binding.TryGetProperty("materialDigest", out JsonElement materialDigest)
                || materialDigest.GetString() != source.MaterialDigest
                || !binding.TryGetProperty("declarationDigest", out JsonElement declarationDigest)
                || declarationDigest.GetString() != source.DeclarationDigest
                || !binding.TryGetProperty("securityGeneration", out JsonElement securityGeneration)
                || !securityGeneration.TryGetInt32(out var generation)
                || generation != source.SecurityGeneration
                || !binding.TryGetProperty("archiveEvidenceId", out JsonElement archiveEvidenceId)
                || archiveEvidenceId.GetString() != source.ArchiveEvidenceId.ToString("D")
                || !binding.TryGetProperty("malwareEvidenceId", out JsonElement malwareEvidenceId)
                || malwareEvidenceId.GetString() != source.MalwareEvidenceId.ToString("D")
                || !binding.TryGetProperty("stage", out JsonElement stage)
                || stage.GetString() != artifact.Stage
                || !binding.TryGetProperty("snapshotId", out JsonElement snapshotId)
                || snapshotId.GetString() != policy.ReferenceSnapshotId?.Trim()
                || !binding.TryGetProperty("snapshotRevision", out JsonElement snapshotRevision)
                || snapshotRevision.GetString() != ReadToolIdentity(policy, "referenceRevision")
                || !binding.TryGetProperty("modelKey", out JsonElement modelKey)
                || modelKey.GetString() != policy.ModelKey?.Trim()
                || !binding.TryGetProperty("indexKey", out JsonElement indexKey)
                || indexKey.GetString() != policy.IndexKey?.Trim()
                || !binding.TryGetProperty("modelIdentitySha256", out JsonElement modelIdentity)
                || modelIdentity.GetString() != ReadModelIdentityDigest(policy))
            {
                return false;
            }

            var canonical = CanonicalJson.Canonicalize(root);
            return canonical.Length == artifact.PayloadBytes
                && string.Equals(CanonicalJson.Sha256Hex(root), artifact.ArtifactDigest, StringComparison.Ordinal)
                && canonical.Length <= 262144;
        }
        catch (Exception ex) when (ex is JsonException or CanonicalJsonException or InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasExactJsonProperties(JsonElement element, params string[] expected) =>
        element.ValueKind == JsonValueKind.Object
        && element.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal)
            .SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);

    private static string ReadToolIdentity(CodeAnalysisPolicyVersion policy, string member)
    {
        using var document = JsonDocument.Parse(policy.ToolIdentitiesJson);
        return document.RootElement.TryGetProperty(member, out JsonElement value)
            && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }

    private static string ReadModelIdentityDigest(CodeAnalysisPolicyVersion policy) =>
        CodeAnalysisReportValidator.ReadModelIdentityDigest(policy);

    /// <summary>
    /// The licenses must come from the exact declaration revision the input digest was built over.
    /// A run without a declared revision simply has no declared components.
    /// </summary>
    private async Task<IReadOnlyList<CodeAnalysisDeclaredLicense>> ReadLicenses(
        CodeAnalysisRun run,
        CancellationToken cancellationToken)
    {
        if (run.DeclarationRevisionId is not { } revisionId)
        {
            return [];
        }

        AssetSourceDeclarationRevision? revision = await dbContext.AssetSourceDeclarationRevisions
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == revisionId, cancellationToken)
            .ConfigureAwait(false);

        if (revision is null
            || !string.Equals(revision.ContentDigest, run.DeclarationDigest, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The declared revision bound to this analysis run does not match the admitted declaration digest.");
        }

        // Uses the same projector the enqueuer used for the input digest, so the metadata cannot drift.
        return DeclaredLicenseProjector
            .Read(revision.PayloadJson)
            .Select(license => new CodeAnalysisDeclaredLicense(license.ScopePath, license.Spdx))
            .ToList();
    }

    private async Task<bool> RevisionScopeMatches(
        CodeAnalysisRun run,
        CancellationToken cancellationToken)
    {
        // Declaration scope: exact revision digest and same asset/workspace binding.
        if (run.DeclarationRevisionId is { } declarationRevisionId)
        {
            AssetSourceDeclarationRevision? declaration = await dbContext.AssetSourceDeclarationRevisions
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == declarationRevisionId, cancellationToken)
                .ConfigureAwait(false);

            if (declaration is null
                || declaration.AssetId != run.AssetId
                || !string.Equals(declaration.ContentDigest, run.DeclarationDigest, StringComparison.Ordinal))
            {
                return false;
            }

            if (run.AssetDraftWorkspaceId is { } workspaceId
                && declaration.WorkspaceId != workspaceId)
            {
                return false;
            }
        }

        // Material scope: exact revision digest and same asset/workspace binding.
        if (run.MaterialRevisionId is { } materialRevisionId)
        {
            AssetMaterialMetadataRevision? material = await dbContext.AssetMaterialMetadataRevisions
                .AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == materialRevisionId, cancellationToken)
                .ConfigureAwait(false);

            if (material is null
                || material.AssetId != run.AssetId
                || !string.Equals(material.ContentDigest, run.MaterialDigest, StringComparison.Ordinal))
            {
                return false;
            }

            if (run.AssetDraftWorkspaceId is { } workspaceId
                && material.WorkspaceId != workspaceId)
            {
                return false;
            }
        }

        // Security evidence scope: both evidence rows must still exist for the exact
        // asset/version/hash/generation bound to this run.
        SecurityEvidenceRecord? archive = await dbContext.SecurityEvidenceRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == run.ArchiveEvidenceId, cancellationToken)
            .ConfigureAwait(false);

        SecurityEvidenceRecord? malware = await dbContext.SecurityEvidenceRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == run.MalwareEvidenceId, cancellationToken)
            .ConfigureAwait(false);

        if (archive is null
            || malware is null
            || archive.AssetId != run.AssetId
            || archive.AssetVersionId != run.AssetVersionId
            || malware.AssetId != run.AssetId
            || malware.AssetVersionId != run.AssetVersionId
            || !string.Equals(archive.ContentSha256, run.ContentSha256, StringComparison.Ordinal)
            || !string.Equals(malware.ContentSha256, run.ContentSha256, StringComparison.Ordinal)
            || archive.Generation != run.SecurityGeneration
            || malware.Generation != run.SecurityGeneration)
        {
            return false;
        }

        return true;
    }
}
