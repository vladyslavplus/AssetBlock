using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence.Publication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>Owner scoped, seller safe analysis reads and explicit recovery.</summary>
internal sealed class CodeAnalysisSellerStore(
    ApplicationDbContext db,
    IAnalysisRunEnqueuer enqueuer,
    ISecurityProvenanceStore securityProvenance,
    IAuditWriter auditWriter,
    TimeProvider clock,
    IOptions<CodeAnalysisOptions> analysisOptions) : ICodeAnalysisSellerStore
{
    private const int PAGE_SIZE_LIMIT = 50;
    private const string RETRY_OPERATION_KIND = "SELLER_ANALYSIS_RETRY";

    public async Task<SellerAnalysisSummaryDto?> GetOwnerSummary(Guid assetVersionId, Guid ownerUserId, CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        return await GetOwnerSummaryCore(assetVersionId, ownerUserId, lockRows: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SellerAnalysisSummaryDto?> GetOwnerSummaryCore(
        Guid assetVersionId,
        Guid ownerUserId,
        bool lockRows,
        CancellationToken cancellationToken)
    {
        Guid? assetId = await db.AssetVersions.AsNoTracking().Where(v => v.Id == assetVersionId)
            .Select(v => (Guid?)v.AssetId).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (assetId is null)
        {
            return null;
        }

        Asset? asset = lockRows
            ? await db.Assets.FromSqlInterpolated($"SELECT * FROM assets WHERE \"Id\" = {assetId.Value} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await db.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == assetId.Value, cancellationToken).ConfigureAwait(false);
        AssetDraftWorkspace? latestWorkspace = lockRows
            ? await db.AssetDraftWorkspaces.FromSqlInterpolated($"SELECT * FROM asset_draft_workspaces WHERE \"AssetId\" = {assetId.Value} AND \"AssetVersionId\" = {assetVersionId} FOR UPDATE")
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await db.AssetDraftWorkspaces.AsNoTracking()
                .SingleOrDefaultAsync(w => w.AssetId == assetId.Value && w.AssetVersionId == assetVersionId, cancellationToken).ConfigureAwait(false);
        AssetVersion? version = lockRows
            ? await db.AssetVersions.FromSqlInterpolated($"SELECT * FROM asset_versions WHERE \"Id\" = {assetVersionId} FOR UPDATE")
                .Include(v => v.Asset).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await db.AssetVersions.AsNoTracking().Include(v => v.Asset)
                .SingleOrDefaultAsync(v => v.Id == assetVersionId, cancellationToken).ConfigureAwait(false);
        if (asset is null || asset.AuthorId != ownerUserId || asset.DeletedAt is not null
            || version is null || version.AssetId != assetId || version.Asset.AuthorId != ownerUserId || version.Asset.DeletedAt is not null)
        {
            return null;
        }

        CodeAnalysisRun? run = await db.CodeAnalysisRuns.AsNoTracking()
            .Where(r => r.AssetVersionId == assetVersionId && r.SelectionGeneration == version.AnalysisSelectionGeneration)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        CodeAnalysisReportHeader? currentReport = run is null ? null : await OwnerAdmittedReports()
            .Where(h => h.AssetVersionId == assetVersionId && h.AnalysisRunId == run.Id)
            .OrderByDescending(h => h.FinalizedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var currentAdmitted = currentReport is not null;

        CodeAnalysisReportHeader? historicalReport = currentAdmitted ? currentReport : await OwnerAdmittedReports()
            .Where(h => h.AssetVersionId == assetVersionId)
            .OrderByDescending(h => h.FinalizedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        Guid? currentMaterial = latestWorkspace is null || latestWorkspace.MaterialMetadataHeadRevision == 0 ? null
            : await db.AssetMaterialMetadataRevisions.AsNoTracking().Where(r => r.WorkspaceId == latestWorkspace.Id
                && r.Revision == latestWorkspace.MaterialMetadataHeadRevision).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        Guid? currentDeclaration = latestWorkspace is null || latestWorkspace.SourceDeclarationHeadRevision == 0 ? null
            : await db.AssetSourceDeclarationRevisions.AsNoTracking().Where(r => r.WorkspaceId == latestWorkspace.Id
                && r.Revision == latestWorkspace.SourceDeclarationHeadRevision).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var currentInputTuple = run is not null && latestWorkspace is not null
            && run.SelectionGeneration == version.AnalysisSelectionGeneration
            && run.AssetDraftWorkspaceId == latestWorkspace.Id
            && run.MaterialRevisionId == currentMaterial && run.DeclarationRevisionId == currentDeclaration
            && run.ContentSha256 == version.ContentSha256
            && run.PolicyDigest == analysisOptions.Value.PolicyDigest
            && !string.IsNullOrWhiteSpace(analysisOptions.Value.PolicyKey)
            && await db.CodeAnalysisPolicyVersions.AsNoTracking().AnyAsync(p => p.PolicyKey == analysisOptions.Value.PolicyKey
                && p.PolicyDigest == analysisOptions.Value.PolicyDigest, cancellationToken).ConfigureAwait(false);
        var currentTuple = currentInputTuple;
        if (currentInputTuple)
        {
            DateTimeOffset proofNow = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"")
                .FirstAsync(cancellationToken).ConfigureAwait(false);
            SecurityProofVerdict proof = await CurrentSecurityProof.Verify(db, new SecurityProofExpectation(
                run!.AssetId, run.AssetVersionId, run.ContentSha256, run.SecurityRecheckId, run.ArchiveEvidenceId,
                run.MalwareEvidenceId, run.SecurityGeneration), version.ContentLength, proofNow, cancellationToken).ConfigureAwait(false);
            currentTuple = proof.Accepted;
        }
        CodeAnalysisReportHeader? report = currentAdmitted ? currentReport : historicalReport;
        var findingDetailsAvailable = report is null;
        if (report is not null)
        {
            var findingRowCount = await db.CodeAnalysisReportFindings.AsNoTracking()
                .CountAsync(f => f.ReportId == report.Id, cancellationToken).ConfigureAwait(false);
            findingDetailsAvailable = report.FindingCount.HasValue && findingRowCount == report.FindingCount.Value;
        }

        var recheckPending = await db.SecurityRechecks.AsNoTracking()
            .AnyAsync(r => r.AssetVersionId == assetVersionId && r.SelectedGeneration != null
                && (r.State == SecurityRecheckState.QUEUED || r.State == SecurityRecheckState.INSPECTING
                    || r.State == SecurityRecheckState.SCANNING), cancellationToken).ConfigureAwait(false);
        var schedulingPending = await db.Database.SqlQueryRaw<bool>("""
            SELECT EXISTS (
                SELECT 1 FROM outbox_messages
                WHERE "Type" = 'AnalysisScheduling' AND "Status" = 0
                  AND "Payload"::jsonb ->> 'assetVersionId' = {0}
            ) AS "Value"
            """, assetVersionId.ToString()).FirstAsync(cancellationToken).ConfigureAwait(false);

        var retry = run is { State: CodeAnalysisRunState.FAILED, OwnerRetryable: true }
                    && version is { SafetyRestriction: AssetVersionSafetyRestriction.NONE, ProcessingStatus: AssetVersionProcessingStatus.READY, IsCurrent: true } && latestWorkspace is not null && currentInputTuple && !recheckPending;
        var reportIsCurrent = currentAdmitted && currentTuple && currentReport is not null
            && version.SelectedCodeAnalysisReportId == currentReport.Id;
        SellerAnalysisLifecycle lifecycle = reportIsCurrent
            ? SellerAnalysisLifecycle.COMPLETED
            : recheckPending || schedulingPending ? SellerAnalysisLifecycle.QUEUED
            : run?.State switch
            {
                CodeAnalysisRunState.QUEUED => SellerAnalysisLifecycle.QUEUED,
                CodeAnalysisRunState.RUNNING => SellerAnalysisLifecycle.RUNNING,
                CodeAnalysisRunState.FAILED => SellerAnalysisLifecycle.FAILED,
                CodeAnalysisRunState.CANCELLED => SellerAnalysisLifecycle.CANCELLED,
                _ => SellerAnalysisLifecycle.NOT_AVAILABLE
            };
        SellerAnalysisStageDto[] stages = report is null ? Array.Empty<SellerAnalysisStageDto>() : await db.CodeAnalysisReportStages.AsNoTracking()
            .Where(s => s.ReportId == report.Id)
            .OrderBy(s => s.Stage)
            .Select(s => new SellerAnalysisStageDto(s.Stage, s.Applicability.ToString(), s.Outcome.ToString(), s.Retryable))
            .ToArrayAsync(cancellationToken).ConfigureAwait(false);
        string[] limitations = ["Pair verification is disabled by evaluation. Retained comparison evidence requires human review.", "Publication requires an explicit moderator decision."];
        var submissionBlockedReason = !reportIsCurrent || latestWorkspace is null
            ? ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE
            : await ReviewSubmissionEligibility.BlockedReason(db, version, latestWorkspace, report!.Id, cancellationToken).ConfigureAwait(false);
        var reviewEligible = report is not null && report.PolicyVersion == ReviewAnalysisPolicy.KEY
            && await db.CodeAnalysisReportAdmissions.AnyAsync(a => a.ReportId == report.Id
                && a.AdmissionKind == CodeAnalysisAdmissionKind.SERVER_VALIDATED && a.ReviewEligible
                && a.AdmittedBy == "assetblock-dotnet-finalizer", cancellationToken).ConfigureAwait(false);
        SellerAnalysisRetryExpectation? expectation = retry && run is not null && latestWorkspace is not null
            ? new SellerAnalysisRetryExpectation(run.Id, latestWorkspace.WorkspaceRevision, run.PolicyDigest, run.InputDigest)
            : null;
        return new SellerAnalysisSummaryDto(
            version.AssetId, assetVersionId, lifecycle, run?.Id, report?.Id,
            reportIsCurrent && report?.Id == currentReport?.Id,
            report?.CoverageLevel, report?.AnalyzedFileCount ?? 0, report?.OmissionCount ?? 0, report?.FindingCount ?? 0,
            findingDetailsAvailable,
            run?.CreatedAt, run?.CompletedAt, SafeErrorCode(run?.ErrorCode), SafeErrorMessage(run?.ErrorCode),
            retry ? "Retry analysis after checking the current seller declaration and materials." : null,
            retry, expectation, submissionBlockedReason is null, reviewEligible, "Selected source files from this version only", limitations, stages, submissionBlockedReason);
    }

    public async Task<SellerAnalysisFindingsPageDto?> GetOwnerFindings(Guid assetVersionId, Guid reportId, Guid ownerUserId, int page, CancellationToken cancellationToken = default)
    {
        if (page is < 1 or > 1_000_000)
        {
            return null;
        }

        // Count, summary visibility, and page rows must all observe one MVCC snapshot. Retention
        // locks the asset before deleting details, so a concurrent purge is either wholly visible
        // here or wholly after this read.
        await using IDbContextTransaction snapshot = await db.Database
            .BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);

        var report = await OwnerAdmittedReports()
            .Where(h => h.Id == reportId && h.AssetVersionId == assetVersionId
                && h.Asset.AuthorId == ownerUserId && h.Asset.DeletedAt == null)
            .Select(h => new { Header = h, h.AssetId, h.AssetVersionId })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (report is null)
        {
            return null;
        }

        var total = await db.CodeAnalysisReportFindings.AsNoTracking().CountAsync(f => f.ReportId == reportId, cancellationToken).ConfigureAwait(false);
        var detailsAvailable = report.Header.FindingCount.HasValue && report.Header.FindingCount.Value == total;
        SellerAnalysisSummaryDto? summary = await GetOwnerSummaryCore(assetVersionId, ownerUserId,
            lockRows: false, cancellationToken).ConfigureAwait(false);
        var isCurrent = summary?.ReportIsCurrent == true && summary.ReportId == reportId;
        if (!detailsAvailable)
        {
            return new SellerAnalysisFindingsPageDto(report.AssetId, assetVersionId, reportId, isCurrent,
                report.Header.CoverageLevel ?? CodeAnalysisCoverageLevel.PARTIAL, Array.Empty<SellerAnalysisFindingDto>(),
                Math.Max(report.Header.FindingCount.GetValueOrDefault(), total), false, page, PAGE_SIZE_LIMIT,
                (Math.Max(report.Header.FindingCount.GetValueOrDefault(), total) + PAGE_SIZE_LIMIT - 1) / PAGE_SIZE_LIMIT);
        }
        List<CodeAnalysisReportFinding> rows = await db.CodeAnalysisReportFindings.AsNoTracking().Where(f => f.ReportId == reportId)
            .OrderBy(f => f.OwnScopePath).ThenBy(f => f.OwnStartByte).ThenBy(f => f.FindingKey)
            .Skip((page - 1) * PAGE_SIZE_LIMIT).Take(PAGE_SIZE_LIMIT).ToListAsync(cancellationToken).ConfigureAwait(false);
        SellerAnalysisFindingDto[] items = rows.Select(f => new SellerAnalysisFindingDto(
            f.Id, f.FindingType, f.OwnScopePath, f.OwnExcerpt, f.OwnStartByte, f.OwnEndByte, f.OwnStartLine, f.OwnEndLine,
            f.ReasonText, f.RequestedAction,
            f.ForeignOrigin == "OWN" ? "OWN_PRIOR_VERSION"
                : f is { ForeignOrigin: "FOREIGN", ForeignSensitivity: CodeAnalysisFindingSensitivity.PRIVATE }
                    ? "FOREIGN_PRIVATE"
                    : f.ForeignOrigin == "FOREIGN" ? "FOREIGN_PUBLIC" : null,
            ParsePublicAttribution(f.PublicAttributionJson, f.ForeignOrigin == "OWN" || f.ForeignSensitivity != CodeAnalysisFindingSensitivity.PUBLIC)))
            .ToArray();
        return new SellerAnalysisFindingsPageDto(report.AssetId, assetVersionId, reportId, isCurrent,
            report.Header.CoverageLevel ?? CodeAnalysisCoverageLevel.PARTIAL, items, total, true, page, PAGE_SIZE_LIMIT,
            (total + PAGE_SIZE_LIMIT - 1) / PAGE_SIZE_LIMIT);
    }

    public async Task<SellerAnalysisRetryStoreResult> Retry(Guid assetId, Guid assetVersionId, Guid ownerUserId, SellerAnalysisRetryRequest request, CancellationToken cancellationToken = default)
    {
        if (request.OperationId == Guid.Empty || request.ExpectedRunId == Guid.Empty || request.ExpectedWorkspaceRevision < 1
            || string.IsNullOrWhiteSpace(request.ExpectedInputDigest) || string.IsNullOrWhiteSpace(request.ExpectedPolicyDigest))
        {
            return new(SellerAnalysisRetryStatus.CONFLICT);
        }

        var requestDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { assetId, assetVersionId, request.ExpectedRunId, request.ExpectedWorkspaceRevision, request.ExpectedPolicyDigest, request.ExpectedInputDigest })))).ToLowerInvariant();
        await using IDbContextTransaction transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        Asset? lockedAsset = await db.Assets.FromSqlInterpolated($"SELECT * FROM assets WHERE \"Id\" = {assetId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (lockedAsset is null || lockedAsset.AuthorId != ownerUserId || lockedAsset.DeletedAt is not null)
        {
            return new(SellerAnalysisRetryStatus.NOT_FOUND);
        }

        AssetDraftWorkspace? workspace = await db.AssetDraftWorkspaces.FromSqlInterpolated($"SELECT * FROM asset_draft_workspaces WHERE \"AssetId\" = {assetId} AND \"AssetVersionId\" = {assetVersionId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        AssetVersion? target = await db.AssetVersions.FromSqlInterpolated($"SELECT * FROM asset_versions WHERE \"Id\" = {assetVersionId} FOR UPDATE")
            .Include(v => v.Asset).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (target is null || target.AssetId != assetId || target.Asset.AuthorId != ownerUserId || target.Asset.DeletedAt is not null)
        {
            return new(SellerAnalysisRetryStatus.NOT_FOUND);
        }

        var receiptLockKey = $"{ownerUserId:N}:{request.OperationId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({receiptLockKey}, 0))", cancellationToken)
            .ConfigureAwait(false);

        JsonMutationIdempotencyRecord? prior = await db.JsonMutationIdempotencyRecords.FromSqlInterpolated($"""
            SELECT * FROM json_mutation_idempotency
            WHERE "ActorUserId" = {ownerUserId} AND "OperationKind" = {RETRY_OPERATION_KIND} AND "OperationId" = {request.OperationId}
            FOR UPDATE
            """).AsNoTracking().SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (prior is not null)
        {
            if (prior.RequestDigest != requestDigest)
            {
                return new(SellerAnalysisRetryStatus.CONFLICT);
            }

            SellerAnalysisRetryAcceptedDto? replay = JsonSerializer.Deserialize<SellerAnalysisRetryAcceptedDto>(prior.ResultJson);
            if (replay is null || replay.OperationId != request.OperationId || replay.AssetId != assetId || replay.AssetVersionId != assetVersionId
                || replay.Status is not (SellerAnalysisRetryStatus.ACCEPTED or SellerAnalysisRetryStatus.RECHECK_QUEUED))
            {
                return new(SellerAnalysisRetryStatus.CONFLICT);
            }

            return new(replay.Status, replay with { Replayed = true }, replay.SecurityRecheckId);
        }

        SecurityRecheck? existingRecheck = await db.SecurityRechecks.AsNoTracking()
            .SingleOrDefaultAsync(r => r.OperationReceiptId == request.OperationId, cancellationToken)
            .ConfigureAwait(false);
        if (existingRecheck is not null)
        {
            if (existingRecheck.AssetId != assetId || existingRecheck.AssetVersionId != assetVersionId
                || existingRecheck.OperationDigest != requestDigest)
            {
                return new(SellerAnalysisRetryStatus.CONFLICT);
            }

            if (existingRecheck.State is SecurityRecheckState.QUEUED or SecurityRecheckState.INSPECTING or SecurityRecheckState.SCANNING)
            {
                SellerAnalysisRetryAcceptedDto pending = new(request.OperationId, assetId, assetVersionId,
                    SellerAnalysisRetryStatus.RECHECK_QUEUED, SecurityRecheckId: existingRecheck.Id, Replayed: true);
                return new(SellerAnalysisRetryStatus.RECHECK_QUEUED, pending, existingRecheck.Id);
            }

            CodeAnalysisRun? recheckRun = await db.CodeAnalysisRuns.AsNoTracking()
                .Where(r => r.ParentRunId == request.ExpectedRunId && r.SecurityRecheckId == existingRecheck.Id)
                .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (recheckRun is not null && recheckRun.JobId is { } recheckJobId)
            {
                SellerAnalysisRetryAcceptedDto acceptedAfterRecheck = new(request.OperationId, assetId, assetVersionId,
                    SellerAnalysisRetryStatus.ACCEPTED, recheckRun.Id, recheckJobId, existingRecheck.Id, Replayed: true);
                return new(SellerAnalysisRetryStatus.ACCEPTED, acceptedAfterRecheck, existingRecheck.Id);
            }

            return new(SellerAnalysisRetryStatus.CONFLICT);
        }

        CodeAnalysisRun? failed = await db.CodeAnalysisRuns.FromSqlInterpolated($"SELECT * FROM code_analysis_runs WHERE \"Id\" = {request.ExpectedRunId} AND \"AssetVersionId\" = {assetVersionId} FOR UPDATE")
            .AsNoTracking()
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        Guid? currentMaterialId = workspace is null || workspace.MaterialMetadataHeadRevision == 0 ? null
            : await db.AssetMaterialMetadataRevisions.AsNoTracking().Where(r => r.WorkspaceId == workspace.Id
                && r.Revision == workspace.MaterialMetadataHeadRevision).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        Guid? currentDeclarationId = workspace is null || workspace.SourceDeclarationHeadRevision == 0 ? null
            : await db.AssetSourceDeclarationRevisions.AsNoTracking().Where(r => r.WorkspaceId == workspace.Id
                && r.Revision == workspace.SourceDeclarationHeadRevision).Select(r => (Guid?)r.Id).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (workspace is null || failed is null || workspace.WorkspaceRevision != request.ExpectedWorkspaceRevision
            || failed.PolicyDigest != request.ExpectedPolicyDigest || failed.InputDigest != request.ExpectedInputDigest
            || failed.State != CodeAnalysisRunState.FAILED || target.ProcessingStatus != AssetVersionProcessingStatus.READY
            || failed.SelectionGeneration != target.AnalysisSelectionGeneration
            || !target.IsCurrent || failed.AssetDraftWorkspaceId != workspace.Id
            || failed.MaterialRevisionId != currentMaterialId || failed.DeclarationRevisionId != currentDeclarationId
            || !string.Equals(failed.ContentSha256, target.ContentSha256, StringComparison.Ordinal)
            || target.SafetyRestriction != AssetVersionSafetyRestriction.NONE
            || !failed.OwnerRetryable
            || failed.PolicyDigest != analysisOptions.Value.PolicyDigest
            || string.IsNullOrWhiteSpace(analysisOptions.Value.PolicyKey)
            || !await db.CodeAnalysisPolicyVersions.AsNoTracking().AnyAsync(p => p.PolicyKey == analysisOptions.Value.PolicyKey
                && p.PolicyDigest == analysisOptions.Value.PolicyDigest, cancellationToken).ConfigureAwait(false))
        {
            return new(SellerAnalysisRetryStatus.CONFLICT);
        }

        AssetProcessingJob? failedJob = failed.JobId is null ? null : await db.AssetProcessingJobs
            .FromSqlInterpolated($"SELECT * FROM asset_processing_jobs WHERE \"Id\" = {failed.JobId} FOR UPDATE")
            .SingleOrDefaultAsync(j => j.AssetId == assetId && j.AssetVersionId == assetVersionId
                && j.AnalysisRunId == failed.Id && j.Type == AssetProcessingJobType.CODE_ANALYSIS, cancellationToken)
            .ConfigureAwait(false);
        if (failedJob?.Status != AssetProcessingJobStatus.FAILED)
        {
            return new(SellerAnalysisRetryStatus.CONFLICT);
        }

        CodeAnalysisRun? latestCurrentRun = await db.CodeAnalysisRuns.AsNoTracking()
            .Where(r => r.AssetVersionId == assetVersionId && r.SelectionGeneration == target.AnalysisSelectionGeneration)
            .OrderByDescending(r => r.CreatedAt).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (latestCurrentRun?.Id != failed.Id || target.SelectedCodeAnalysisReportId is { } selectedReportId
            && await db.CodeAnalysisReportHeaders.AsNoTracking().AnyAsync(h => h.Id == selectedReportId && h.AnalysisRunId != failed.Id, cancellationToken).ConfigureAwait(false))
        {
            return new(SellerAnalysisRetryStatus.CONFLICT);
        }

        SecurityRecheck? selectedRecheck = await db.SecurityRechecks.AsNoTracking()
            .SingleOrDefaultAsync(r => r.AssetVersionId == assetVersionId && r.SelectedGeneration != null, cancellationToken)
            .ConfigureAwait(false);
        if (selectedRecheck is not null && selectedRecheck.State is
            (SecurityRecheckState.QUEUED or SecurityRecheckState.INSPECTING or SecurityRecheckState.SCANNING))
        {
            return new(SellerAnalysisRetryStatus.CONFLICT);
        }

        if (selectedRecheck is { State: SecurityRecheckState.ERROR })
        {
            return await QueueProofRepair(assetId, assetVersionId, ownerUserId, target.ContentSha256,
                request.OperationId, requestDigest, failed.Id, selectedRecheck.Id, transaction, cancellationToken)
                .ConfigureAwait(false);
        }

        Guid? selectedRecheckId = null;
        Guid archiveEvidenceId = failed.ArchiveEvidenceId;
        Guid malwareEvidenceId = failed.MalwareEvidenceId;
        var securityGeneration = failed.SecurityGeneration;
        if (selectedRecheck is not null)
        {
            if (selectedRecheck.State != SecurityRecheckState.SUCCEEDED
                || selectedRecheck.CompletedGeneration != selectedRecheck.SecurityGeneration
                || selectedRecheck.ArchiveEvidenceId is not { } selectedArchive
                || selectedRecheck.MalwareEvidenceId is not { } selectedMalware
                || selectedRecheck.ContentSha256 != target.ContentSha256)
            {
                return new(SellerAnalysisRetryStatus.CONFLICT);
            }

            selectedRecheckId = selectedRecheck.Id;
            archiveEvidenceId = selectedArchive;
            malwareEvidenceId = selectedMalware;
            securityGeneration = selectedRecheck.SecurityGeneration;
        }

        DateTimeOffset dbNow = clock.GetUtcNow();
        AnalysisRunEnqueueResult queued = await enqueuer.Enqueue(assetId, assetVersionId, selectedRecheckId, archiveEvidenceId,
            malwareEvidenceId, securityGeneration, target.ContentSha256, dbNow, failed.Id, cancellationToken).ConfigureAwait(false);
        if (queued.AnalysisRunId == Guid.Empty && queued.Rejection == AnalysisEnqueueRejection.PROOF_INVALID)
        {
            return await QueueProofRepair(assetId, assetVersionId, ownerUserId, target.ContentSha256,
                request.OperationId, requestDigest, failed.Id, parentRecheckId: null, transaction, cancellationToken)
                .ConfigureAwait(false);
        }
        if (queued.AnalysisRunId == Guid.Empty)
        {
            return new(SellerAnalysisRetryStatus.CONFLICT);
        }

        SellerAnalysisRetryAcceptedDto accepted = new(request.OperationId, assetId, assetVersionId,
            SellerAnalysisRetryStatus.ACCEPTED, queued.AnalysisRunId, queued.AnalysisJobId);
        await auditWriter.Write(new AuditEvent(
            "SELLER_ANALYSIS_RETRY", AuditOutcome.SUCCESS, "ASSET_VERSION", assetVersionId.ToString(),
            new Dictionary<string, object?> { ["operationId"] = request.OperationId, ["runId"] = queued.AnalysisRunId },
            AuditActorType.USER, ownerUserId), cancellationToken).ConfigureAwait(false);
        db.JsonMutationIdempotencyRecords.Add(CreateReceipt(ownerUserId, request.OperationId, requestDigest, accepted));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(SellerAnalysisRetryStatus.ACCEPTED, accepted);
    }

    private async Task<SellerAnalysisRetryStoreResult> QueueProofRepair(
        Guid assetId,
        Guid assetVersionId,
        Guid ownerUserId,
        string contentSha256,
        Guid operationId,
        string requestDigest,
        Guid parentAnalysisRunId,
        Guid? parentRecheckId,
        IDbContextTransaction transaction,
        CancellationToken cancellationToken)
    {
        SecurityRecheckStartResult recheck = await securityProvenance.StartSecurityRecheck(
            new SecurityRecheckStartRequest(assetId, assetVersionId, contentSha256,
                AssetProcessingDefaults.MALWARE_POLICY_VERSION, AssetProcessingDefaults.DEFINITION_VERSION,
                requestDigest, operationId, parentRecheckId, parentAnalysisRunId), cancellationToken)
            .ConfigureAwait(false);
        if (recheck.RecheckId == Guid.Empty || recheck.ArchiveJobId == Guid.Empty)
        {
            return new(SellerAnalysisRetryStatus.CONFLICT);
        }

        SecurityRecheck? exactRecheck = await db.SecurityRechecks.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == recheck.RecheckId && r.AssetId == assetId
                && r.AssetVersionId == assetVersionId && r.OperationReceiptId == operationId
                && r.OperationDigest == requestDigest && r.ParentAnalysisRunId == parentAnalysisRunId
                && r.ParentRecheckId == parentRecheckId, cancellationToken)
            .ConfigureAwait(false);
        if (exactRecheck is null)
        {
            return new(SellerAnalysisRetryStatus.CONFLICT);
        }

        SellerAnalysisRetryAcceptedDto accepted = new(operationId, assetId, assetVersionId,
            SellerAnalysisRetryStatus.RECHECK_QUEUED, SecurityRecheckId: recheck.RecheckId);
        await auditWriter.Write(new AuditEvent(
            "SELLER_ANALYSIS_RETRY", AuditOutcome.SUCCESS, "ASSET_VERSION", assetVersionId.ToString(),
            new Dictionary<string, object?> { ["operationId"] = operationId, ["status"] = "RECHECK_QUEUED" },
            AuditActorType.USER, ownerUserId), cancellationToken).ConfigureAwait(false);
        db.JsonMutationIdempotencyRecords.Add(CreateReceipt(ownerUserId, operationId, requestDigest, accepted));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(SellerAnalysisRetryStatus.RECHECK_QUEUED, accepted, recheck.RecheckId);
    }

    private static string? SafeErrorCode(string? code) => code is null ? null : code.Length <= 80 && code.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? code : "ANALYSIS_FAILED";

    private IQueryable<CodeAnalysisReportHeader> OwnerAdmittedReports() =>
        PublicationEligibilityQuery.AdmittedReports(db)
            .Where(h => h.Purpose == CodeAnalysisReportPurpose.PRODUCTION
                && db.CodeAnalysisReportAdmissions.Any(a => a.ReportId == h.Id
                && a.AdmissionKind == CodeAnalysisAdmissionKind.SERVER_VALIDATED
                && a.ReviewEligible == h.ReviewEligible && a.AdmittedBy == "assetblock-dotnet-finalizer"
                && a.ReportDigest == h.ReportDigest && a.PythonReportId == h.PythonReportId));
    private static JsonMutationIdempotencyRecord CreateReceipt(Guid ownerUserId, Guid operationId, string requestDigest,
        SellerAnalysisRetryAcceptedDto accepted) => new()
        {
            Id = Guid.NewGuid(),
            ActorUserId = ownerUserId,
            OperationKind = RETRY_OPERATION_KIND,
            OperationId = operationId,
            RequestDigest = requestDigest,
            ResultJson = JsonSerializer.Serialize(accepted)
        };
    private static string? SafeErrorMessage(string? code) => code is null ? null : "Analysis could not complete. Review the current version details before retrying.";
    private static SellerPublicAttributionDto? ParsePublicAttribution(string? json, bool privateHit)
    {
        if (privateHit || string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var label = doc.RootElement.TryGetProperty("label", out JsonElement labelNode) ? labelNode.GetString() ?? "Public reference" : "Public reference";
            var url = doc.RootElement.TryGetProperty("url", out JsonElement node) ? node.GetString() : null;
            Uri? uri = null;
            if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out Uri? candidate)
                && candidate.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(candidate.UserInfo))
            {
                uri = candidate;
            }

            return string.IsNullOrWhiteSpace(label) ? null
                : new SellerPublicAttributionDto(label[..Math.Min(label.Length, 120)], uri?.ToString());
        }
        catch (JsonException) { return null; }
    }
}
