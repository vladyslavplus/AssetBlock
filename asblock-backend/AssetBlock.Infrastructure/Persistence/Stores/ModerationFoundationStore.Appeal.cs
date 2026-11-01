using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed partial class ModerationFoundationStore
{
    public async Task<GuardedSubmissionResult> RequestAppeal(AppealCreateRequest request, CancellationToken cancellationToken = default)
    {
        GuardedSubmissionResult result = new(GuardedSubmissionStatus.BLOCKED, null, ErrorCodes.ERR_FORBIDDEN);
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                Asset? asset = await assetStore.GetForUpdate(request.AssetId, ct);
                if (asset is null || asset.DeletedAt is not null || asset.AuthorId != request.OwnerUserId)
                { return; }
                AssetDraftWorkspace? workspace = await LockReviewWorkspace(request.WorkspaceId, ct);
                if (workspace is null || workspace.AssetId != request.AssetId)
                { return; }
                await dbContext.Database.SqlQuery<Guid>($"""
                    SELECT "Id" AS "Value" FROM moderation_submissions WHERE "AssetVersionId" = {request.AssetVersionId}
                      AND ("State" IN ('SUBMITTED', 'IN_REVIEW') OR "Id" = (
                          SELECT "Id" FROM moderation_submissions WHERE "AssetVersionId" = {request.AssetVersionId}
                          ORDER BY "CreatedAt" DESC, "Id" DESC LIMIT 1))
                    ORDER BY "Id" FOR UPDATE
                    """).ToListAsync(ct);
                UserPersistedRole? role = await LockRoleAfterWorkspace(request.OwnerUserId, ct);
                if (!CanActAsOwner(role))
                { return; }
                JsonMutationIdempotencyRecord? receipt = await FindIdempotencyRecord(request.OwnerUserId, ModerationOperationKinds.APPEAL_CREATE, request.OperationId, ct);
                if (receipt is not null)
                {
                    result = receipt.RequestDigest == request.RequestDigest
                        ? DeserializeGuardedSubmissionResult(receipt.ResultJson) with { Status = GuardedSubmissionStatus.REPLAYED }
                        : new(GuardedSubmissionStatus.CONFLICT, null, ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT);
                    return;
                }
                ModerationSubmission? prior = await LatestSubmissionsForVersion(request.AssetVersionId).FirstOrDefaultAsync(ct);
                if (prior?.Kind == ModerationSubmissionKind.APPEAL)
                { result = new(GuardedSubmissionStatus.CONFLICT, null, ErrorCodes.ERR_MODERATION_APPEAL_EXISTS); return; }
                if (workspace.AssetVersionId != request.AssetVersionId || workspace.WorkspaceVersionScopeKey != request.AssetVersionId
                    || workspace.WorkspaceRevision != request.ExpectedWorkspaceRevision || workspace.CaseRevision != request.ExpectedCaseRevision)
                { result = new(GuardedSubmissionStatus.CONFLICT, null, ErrorCodes.ERR_MODERATION_WORKSPACE_STALE); return; }
                AppealEligibility eligibility = await EvaluateAppealEligibility(request.AssetId, request.AssetVersionId, workspace, prior, ct);
                if (eligibility.Reason is not null)
                { result = new(eligibility.Blocked ? GuardedSubmissionStatus.BLOCKED : GuardedSubmissionStatus.CONFLICT, null, eligibility.Reason); return; }
                prior = eligibility.Prior!;
                ModerationDecisionRecord decision = eligibility.Decision!;
                AssetVersion version = eligibility.Version!;
                var rationale = request.Rationale?.Trim();
                if (string.IsNullOrEmpty(rationale) || rationale.Length > SellerEvidenceLimits.TEXT_MAX_LENGTH || rationale.Contains('\0'))
                { result = new(GuardedSubmissionStatus.BLOCKED, null, ErrorCodes.ERR_VALIDATION_FAILED); return; }
                var evidenceDigest = workspace.SellerEvidenceHeadRevision == 0
                    ? ProductDigests.EmptyProjectionDigest(nameof(AssetSellerEvidenceRevision))
                    : await dbContext.AssetSellerEvidenceRevisions.Where(r => r.WorkspaceId == workspace.Id && r.Revision == workspace.SellerEvidenceHeadRevision).Select(r => r.ContentDigest).SingleAsync(ct);
                DateTimeOffset now = _timeProvider.GetUtcNow();
                workspace.CaseRevision++;
                var appeal = new ModerationSubmission
                {
                    Id = Guid.NewGuid(),
                    Kind = ModerationSubmissionKind.APPEAL,
                    AppealOfDecisionId = decision.Id,
                    AppealRationale = rationale,
                    AssetId = prior.AssetId,
                    AssetVersionId = prior.AssetVersionId,
                    WorkspaceId = workspace.Id,
                    WorkspaceVersionScopeKey = version.Id,
                    OwnerUserId = request.OwnerUserId,
                    ContentSha256 = prior.ContentSha256,
                    CodeAnalysisReportHeaderId = prior.CodeAnalysisReportHeaderId,
                    CoverageAssessmentId = prior.CoverageAssessmentId,
                    DeclarationRevision = prior.DeclarationRevision,
                    MaterialMetadataRevision = prior.MaterialMetadataRevision,
                    SellerEvidenceRevision = workspace.SellerEvidenceHeadRevision,
                    SellerEvidenceDigest = evidenceDigest,
                    PolicyVersion = prior.PolicyVersion,
                    State = ModerationSubmissionState.SUBMITTED,
                    CaseRevision = workspace.CaseRevision,
                    PreviousSubmissionId = prior.Id,
                    CreatedAt = now
                };
                dbContext.ModerationSubmissions.Add(appeal);
                EnqueueLifecycleNotification(appeal, NotificationKind.MODERATION_SUBMITTED, null, now);
                dbContext.ModerationSubmissionHistoryEntries.Add(new ModerationSubmissionHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    SubmissionId = appeal.Id,
                    AssetId = appeal.AssetId,
                    AssetVersionId = appeal.AssetVersionId,
                    State = appeal.State,
                    CaseRevision = appeal.CaseRevision,
                    ActorUserId = request.OwnerUserId,
                    Summary = "Appeal submitted for human review.",
                    CreatedAt = now
                });
                result = new(GuardedSubmissionStatus.CREATED, appeal.Id, null);
                dbContext.JsonMutationIdempotencyRecords.Add(new JsonMutationIdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = request.OwnerUserId,
                    OperationKind = ModerationOperationKinds.APPEAL_CREATE,
                    OperationId = request.OperationId,
                    RequestDigest = request.RequestDigest,
                    ResultJson = SerializeGuardedSubmissionResult(result),
                    CreatedAt = now
                });
                await dbContext.SaveChangesAsync(ct);
                await MandatoryReviewAudit(AuditActions.APPEAL_OPENED, request.OwnerUserId, appeal.Id, AuditOutcome.SUCCESS, new { submissionId = appeal.Id, decisionId = decision.Id }, ct, false);
            }, cancellationToken);
        }
        catch (RequiredReviewAuditException)
        { return new(GuardedSubmissionStatus.BLOCKED, null, ErrorCodes.ERR_REVIEW_AUDIT_UNAVAILABLE); }
        return result;
    }
    private sealed record AppealEligibility(ModerationSubmission? Prior, ModerationDecisionRecord? Decision, AssetVersion? Version, string? Reason, bool Blocked = false);

    private async Task<AppealEligibility> EvaluateAppealEligibility(Guid assetId, Guid versionId, AssetDraftWorkspace workspace, ModerationSubmission? prior, CancellationToken ct)
    {
        if (prior?.Kind == ModerationSubmissionKind.APPEAL)
        { return new(prior, null, null, ErrorCodes.ERR_MODERATION_APPEAL_EXISTS); }
        ModerationDecisionRecord? decision = prior is null ? null : await dbContext.ModerationDecisionRecords.AsNoTracking().SingleOrDefaultAsync(d => d.SubmissionId == prior.Id, ct);
        if (prior is null || prior.State != ModerationSubmissionState.REJECTED || decision?.Outcome != ModerationSubmissionState.REJECTED)
        { return new(prior, decision, null, ErrorCodes.ERR_MODERATION_APPEAL_NOT_ALLOWED); }
        if (await dbContext.ModerationSubmissions.AnyAsync(s => s.AppealOfDecisionId == decision.Id, ct))
        { return new(prior, decision, null, ErrorCodes.ERR_MODERATION_APPEAL_EXISTS); }
        AssetVersion? version = await dbContext.AssetVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId && v.AssetId == assetId, ct);
        if (version is null || !await HasSameReviewBasis(prior, version, workspace, version.SelectedCodeAnalysisReportId, ct))
        { return new(prior, decision, version, ErrorCodes.ERR_MODERATION_APPEAL_BASIS_CHANGED); }
        var blocked = await ReviewSubmissionEligibility.BlockedReason(dbContext, version, workspace, prior.CodeAnalysisReportHeaderId, ct);
        return new(prior, decision, version, blocked, blocked is not null);
    }
    private async Task<bool> HasSameReviewBasis(ModerationSubmission prior, AssetVersion version, AssetDraftWorkspace workspace, Guid? reportId, CancellationToken ct)
    {
        if (prior.WorkspaceId != workspace.Id || prior.AssetVersionId != version.Id || prior.ContentSha256 != version.ContentSha256
            || prior.CodeAnalysisReportHeaderId != reportId || prior.DeclarationRevision != workspace.SourceDeclarationHeadRevision
            || prior.MaterialMetadataRevision != workspace.MaterialMetadataHeadRevision || prior.PolicyVersion != ReviewAnalysisPolicy.KEY)
        { return false; }
        var digest = await dbContext.CodeAnalysisReportHeaders.AsNoTracking().Where(h => h.Id == prior.CodeAnalysisReportHeaderId)
            .Select(h => dbContext.CodeAnalysisRuns.Where(r => r.Id == h.AnalysisRunId).Select(r => r.PolicyDigest).FirstOrDefault()).SingleOrDefaultAsync(ct);
        return digest == ReviewAnalysisPolicy.DIGEST;
    }

    private async Task<string?> RejectedBasisBlock(AssetVersion version, AssetDraftWorkspace workspace, Guid? reportId, CancellationToken ct)
    {
        ModerationSubmission? latest = await LatestSubmissionsForVersion(version.Id).FirstOrDefaultAsync(ct);
        if (latest is null)
        { return null; }
        if (latest.Kind == ModerationSubmissionKind.APPEAL && latest.State is ModerationSubmissionState.SUBMITTED or ModerationSubmissionState.IN_REVIEW or ModerationSubmissionState.APPROVED)
        { return null; }
        ModerationSubmission? rejected = await dbContext.ModerationDecisionRecords.AsNoTracking()
            .Where(d => d.Submission.AssetVersionId == version.Id && d.Outcome == ModerationSubmissionState.REJECTED)
            .OrderByDescending(d => d.CreatedAt).ThenByDescending(d => d.Id)
            .Select(d => d.Submission).FirstOrDefaultAsync(ct);
        if (rejected is null
            || !await HasSameReviewBasis(rejected, version, workspace, reportId, ct))
        { return null; }
        if (latest.Id != rejected.Id || rejected.Kind == ModerationSubmissionKind.APPEAL || await dbContext.ModerationSubmissions.AnyAsync(s => s.AppealOfDecisionId != null && s.AppealOfDecision!.SubmissionId == rejected.Id, ct))
        { return ErrorCodes.ERR_MODERATION_REJECTION_FINAL; }
        return ErrorCodes.ERR_MODERATION_APPEAL_REQUIRED;
    }
}
