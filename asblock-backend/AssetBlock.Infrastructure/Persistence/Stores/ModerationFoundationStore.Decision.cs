using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Publication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed partial class ModerationFoundationStore
{
    public async Task<ModerationDecisionResult> DecideModerationCase(ModerationDecisionRequest request, CancellationToken cancellationToken = default)
    {
        if (request.ActorId == Guid.Empty || request.SubmissionId == Guid.Empty || request.OperationId == Guid.Empty || request.Basis is null
            || !Enum.IsDefined(request.Outcome) || string.IsNullOrWhiteSpace(request.Rationale) || request.Rationale.Trim().Length > 2000 || request.Rationale.Contains('\0')
            || request.Dispositions is null || request.Dispositions.Count > SellerEvidenceLimits.MAX_RESPONSES || request.Dispositions.Any(d => d is null)
            || request.ManualPlanCompletionReference?.Length > ReviewEvidenceLimits.PLAN_REFERENCE_LENGTH || request.ManualPlanCompletionReference?.Contains('\0') == true)
        { return new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_VALIDATION_FAILED); }
        var binding = await dbContext.ModerationSubmissions.AsNoTracking().Where(s => s.Id == request.SubmissionId)
            .Select(s => new { s.AssetId, s.WorkspaceId }).SingleOrDefaultAsync(cancellationToken);
        if (binding is null)
        { return new(AppRoles.CanReview((await userStore.GetPersistedRole(request.ActorId, cancellationToken))?.Role) ? ModerationDecisionStatus.NOT_FOUND : ModerationDecisionStatus.FORBIDDEN); }
        ModerationDecisionResult result = new(ModerationDecisionStatus.NOT_FOUND);
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                Asset? asset = await assetStore.GetForUpdate(binding.AssetId, ct);
                AssetDraftWorkspace? workspace = await LockReviewWorkspace(binding.WorkspaceId, ct);
                Guid locked = await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM moderation_submissions WHERE "Id" = {request.SubmissionId} FOR UPDATE""").FirstOrDefaultAsync(ct);
                ModerationSubmission? submission = locked == Guid.Empty ? null : await ReadSubmissionFreshUnderLock(locked, ct);
                CoverageAssessment? assessment = submission?.CoverageAssessmentId is Guid assessmentId
                    ? await dbContext.CoverageAssessments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == assessmentId, ct) : null;
                if (assessment is not null)
                { await LockCoverageRequest(assessment.RequestId, ct); }
                UserPersistedRole? actorRole;
                if (assessment is not null && assessment.ModeratorUserId != request.ActorId)
                {
                    (UserPersistedRole? first, UserPersistedRole? second) = await userStore.LockUsersForRoleUpdateInOrder(request.ActorId, assessment.ModeratorUserId, ct);
                    actorRole = first?.UserId == request.ActorId ? first : second;
                }
                else
                { actorRole = await LockRoleAfterWorkspace(request.ActorId, ct); }
                if (!AppRoles.CanReview(actorRole?.Role) || asset?.AuthorId == request.ActorId || submission?.OwnerUserId == request.ActorId)
                { result = new(ModerationDecisionStatus.FORBIDDEN); return; }
                if (asset is null || asset.DeletedAt.HasValue || workspace is null || submission is null)
                { return; }
                Asset? trackedAsset = dbContext.Assets.Local.SingleOrDefault(a => a.Id == asset.Id);
                if (trackedAsset is null)
                { dbContext.Attach(asset); }
                else
                {
                    dbContext.Entry(trackedAsset).CurrentValues.SetValues(asset);
                    dbContext.Entry(trackedAsset).OriginalValues.SetValues(asset);
                    asset = trackedAsset;
                }
                JsonMutationIdempotencyRecord? receipt = await FindIdempotencyRecord(request.ActorId, ModerationOperationKinds.CASE_DECIDE, request.OperationId, ct);
                if (receipt is not null)
                {
                    result = receipt.RequestDigest == request.RequestDigest
                        ? new(ModerationDecisionStatus.SUCCEEDED, JsonSerializer.Deserialize<ModerationDecisionSaved>(receipt.ResultJson, CoverageReviewState.JsonOptions)! with { Replayed = true })
                        : new(ModerationDecisionStatus.OPERATION_CONFLICT, ReasonCode: ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT);
                    return;
                }
                if (await dbContext.ModerationDecisionRecords.AnyAsync(d => d.SubmissionId == submission.Id, ct))
                { result = new(ModerationDecisionStatus.NOT_ACTIVE, ReasonCode: ErrorCodes.ERR_MODERATION_DECISION_EXISTS); return; }
                if (submission.State != ModerationSubmissionState.IN_REVIEW)
                { result = new(ModerationDecisionStatus.NOT_ACTIVE, ReasonCode: ErrorCodes.ERR_MODERATION_CASE_NOT_ACTIVE); return; }
                if (submission.ClaimHolderId != request.ActorId)
                { result = new(ModerationDecisionStatus.NOT_HOLDER); return; }
                DecisionBasis basis = request.Basis;
                AssetVersion? version = await dbContext.AssetVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == submission.AssetVersionId && v.AssetId == asset.Id, ct);
                if (version is not null)
                {
                    AssetVersion? trackedVersion = dbContext.AssetVersions.Local.SingleOrDefault(v => v.Id == version.Id);
                    if (trackedVersion is null)
                    { dbContext.Attach(version); }
                    else
                    {
                        dbContext.Entry(trackedVersion).CurrentValues.SetValues(version);
                        dbContext.Entry(trackedVersion).OriginalValues.SetValues(version);
                        version = trackedVersion;
                    }
                }
                if (version is null || basis.VersionId != submission.AssetVersionId || basis.ContentSha256 != submission.ContentSha256
                    || basis.AnalysisReportId != submission.CodeAnalysisReportHeaderId || basis.DeclarationRevision != submission.DeclarationRevision
                    || basis.MaterialMetadataRevision != submission.MaterialMetadataRevision || basis.PolicyVersion != submission.PolicyVersion
                    || basis.SellerEvidenceRevision != submission.SellerEvidenceRevision || basis.CaseRevision != submission.CaseRevision
                    || workspace.AssetVersionId != version.Id || version.ContentSha256 != submission.ContentSha256
                    || version.SelectedCodeAnalysisReportId != submission.CodeAnalysisReportHeaderId
                    || workspace.SourceDeclarationHeadRevision != submission.DeclarationRevision || workspace.MaterialMetadataHeadRevision != submission.MaterialMetadataRevision
                    || workspace.SellerEvidenceHeadRevision != submission.SellerEvidenceRevision)
                { result = new(ModerationDecisionStatus.STALE, ReasonCode: ErrorCodes.ERR_MODERATION_CASE_STALE); return; }
                var evidenceDigest = workspace.SellerEvidenceHeadRevision == 0
                    ? ProductDigests.EmptyProjectionDigest(nameof(AssetSellerEvidenceRevision))
                    : await dbContext.AssetSellerEvidenceRevisions.AsNoTracking().Where(r => r.WorkspaceId == workspace.Id && r.Revision == workspace.SellerEvidenceHeadRevision)
                        .Select(r => r.ContentDigest).SingleOrDefaultAsync(ct);
                if (evidenceDigest != submission.SellerEvidenceDigest)
                { result = new(ModerationDecisionStatus.STALE, ReasonCode: ErrorCodes.ERR_MODERATION_CASE_STALE); return; }
                var blocked = await ReviewSubmissionEligibility.CurrentReportBlockedReason(dbContext, version, workspace, submission.CodeAnalysisReportHeaderId, ct);
                if (blocked is not null)
                { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: blocked); return; }
                CodeAnalysisReportHeader report = await dbContext.CodeAnalysisReportHeaders.AsNoTracking().SingleAsync(h => h.Id == basis.AnalysisReportId, ct);
                if (report.PolicyVersion != submission.PolicyVersion)
                { result = new(ModerationDecisionStatus.STALE, ReasonCode: ErrorCodes.ERR_MODERATION_CASE_STALE); return; }
                (string? Reason, CoverageOwnerStatus? Coverage) coverageResult = await ReviewSubmissionEligibility.CoverageBlockedReason(dbContext, version, workspace, report, ct);
                if (coverageResult.Reason is not null)
                { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: coverageResult.Reason); return; }
                if (coverageResult.Coverage is not null)
                {
                    if (assessment is null || assessment.RequestId != coverageResult.Coverage.RequestId || assessment.Outcome != CoverageAssessmentOutcome.ACCEPTED)
                    { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_REVIEW_COVERAGE_REQUIRED); return; }
                    // The decision reference must identify the completion recorded by the assessment.
                    if (request.ManualPlanCompletionReference?.Trim() != assessment.CompletedPlanReference)
                    { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_REVIEW_PLAN_REFERENCE_MISMATCH); return; }
                }
                SellerResponseRequirements.Summary requirements = (await SellerResponseRequirements.ReadSummaries(dbContext,
                    [new(workspace.Id, report.Id, workspace.Id, version.Id, workspace.SellerEvidenceHeadRevision)], ct))[workspace.Id];
                Guid[] dispositionIds = request.Dispositions.Select(d => d.FindingId).ToArray();
                if (!requirements.Available)
                { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE); return; }
                if (requirements.Outstanding > 0)
                { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_MODERATION_DECISION_RESPONSES_OUTSTANDING); return; }
                if (dispositionIds.Distinct().Count() != dispositionIds.Length || requirements.Required.Any(id => !dispositionIds.Contains(id)))
                { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_MODERATION_DISPOSITIONS_INVALID); return; }
                if (request.Outcome == ModerationDecisionOutcome.APPROVE && request.Dispositions.Any(d => requirements.Required.Contains(d.FindingId)
                    && d.Disposition is FindingDispositionType.CHANGES_NEEDED or FindingDispositionType.INSUFFICIENT_EVIDENCE))
                { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_MODERATION_DECISION_BLOCKED); return; }
                SellerDraftMaterialPayload? publicMaterial = null;
                Guid[] tagIds = [];
                if (request.Outcome == ModerationDecisionOutcome.APPROVE)
                {
                    AssetMaterialMetadataRevision? material = await dbContext.AssetMaterialMetadataRevisions.AsNoTracking().SingleOrDefaultAsync(m => m.WorkspaceId == workspace.Id && m.Revision == submission.MaterialMetadataRevision, ct);
                    try
                    { publicMaterial = material is null ? null : JsonSerializer.Deserialize<SellerDraftMaterialPayload>(material.PayloadJson, _idempotencyJsonOptions); }
                    catch (JsonException) { }
                    if (publicMaterial is null || publicMaterial.Tags is null || string.IsNullOrWhiteSpace(publicMaterial.Title))
                    { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_PUBLICATION_METADATA_UNAVAILABLE); return; }
                    Guid category = await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM categories WHERE "Id" = {publicMaterial.CategoryId} FOR SHARE""").FirstOrDefaultAsync(ct);
                    var names = publicMaterial.Tags.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                    tagIds = await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM tags WHERE "Name" = ANY ({names}) ORDER BY "Id" FOR SHARE""").ToArrayAsync(ct);
                    if (category == Guid.Empty || tagIds.Length != names.Length)
                    { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_PUBLICATION_METADATA_UNAVAILABLE); return; }
                }
                if (request.Dispositions.Count > 0 && !await AppendFindingDispositions(submission.Id, request.ActorId, basis.CaseRevision, request.Dispositions, ct))
                { result = new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_MODERATION_DISPOSITIONS_INVALID); return; }
                DateTimeOffset now = _timeProvider.GetUtcNow();
                var decisionId = Guid.NewGuid();
                Guid? snapshotId = null;
                var promoted = false;
                if (request.Outcome == ModerationDecisionOutcome.APPROVE)
                {
                    AssetSourceDeclarationRevision declaration = await dbContext.AssetSourceDeclarationRevisions.AsNoTracking().SingleAsync(d => d.WorkspaceId == workspace.Id && d.Revision == submission.DeclarationRevision, ct);
                    SellerDraftMaterialPayload approvedMaterial = publicMaterial!;
                    var currentNumber = await dbContext.PublicationSnapshots.Where(s => s.Id == asset.CurrentPublicationSnapshotId)
                        .Select(s => (int?)s.AssetVersion.VersionNumber).SingleOrDefaultAsync(ct);
                    promoted = currentNumber is null || version.VersionNumber >= currentNumber;
                    snapshotId = Guid.NewGuid();
                    dbContext.PublicationSnapshots.Add(new PublicationSnapshot
                    {
                        Id = snapshotId.Value,
                        AssetId = asset.Id,
                        AssetVersionId = version.Id,
                        ContentSha256 = version.ContentSha256,
                        CodeAnalysisReportHeaderId = report.Id,
                        ModerationSubmissionId = submission.Id,
                        PolicyVersion = submission.PolicyVersion,
                        ApprovedMetadataJson = ApprovedPublicationMetadata.BuildJson(approvedMaterial.Title, approvedMaterial.Description, approvedMaterial.CategoryId, approvedMaterial.Tags),
                        RightsReferenceJson = JsonSerializer.Serialize(new { versionId = version.Id, version.LicenseCode, version.LicenseTemplateVersion, declarationId = declaration.Id, declaration.ContentDigest }, CoverageReviewState.JsonOptions),
                        CreatedAt = now
                    });
                    if (promoted)
                    {
                        await dbContext.AssetVersions.Where(v => v.AssetId == asset.Id && v.IsCurrent && v.Id != version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false), ct);
                        version.IsCurrent = true;
                        asset.CurrentPublicationSnapshotId = snapshotId;
                        asset.Title = approvedMaterial.Title;
                        asset.Description = approvedMaterial.Description;
                        asset.CategoryId = approvedMaterial.CategoryId;
                        asset.SearchRevision++;
                        await dbContext.AssetTags.Where(t => t.AssetId == asset.Id).ExecuteDeleteAsync(ct);
                        foreach (EntityEntry<AssetTag> entry in dbContext.ChangeTracker.Entries<AssetTag>().Where(e => e.Entity.AssetId == asset.Id).ToArray())
                        { entry.State = EntityState.Detached; }
                        dbContext.AssetTags.AddRange(tagIds.Select(id => new AssetTag { AssetId = asset.Id, TagId = id }));
                    }
                }
                ModerationSubmissionState state = request.Outcome switch
                {
                    ModerationDecisionOutcome.APPROVE => ModerationSubmissionState.APPROVED,
                    ModerationDecisionOutcome.REQUEST_CHANGES => ModerationSubmissionState.CHANGES_REQUESTED,
                    _ => ModerationSubmissionState.REJECTED
                };
                dbContext.ModerationDecisionRecords.Add(new ModerationDecisionRecord
                {
                    Id = decisionId,
                    SubmissionId = submission.Id,
                    AssetId = asset.Id,
                    AssetVersionId = version.Id,
                    ModeratorUserId = request.ActorId,
                    Outcome = state,
                    PublicationSnapshotId = snapshotId,
                    CaseRevision = basis.CaseRevision,
                    Message = request.Rationale.Trim(),
                    ManualPlanCompletionReference = request.ManualPlanCompletionReference?.Trim(),
                    CreatedAt = now
                });
                submission.State = state;
                submission.ClaimHolderId = null;
                submission.ClaimedAt = null;
                submission.CaseRevision++;
                workspace.CaseRevision++;
                dbContext.ModerationSubmissionHistoryEntries.Add(new ModerationSubmissionHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    SubmissionId = submission.Id,
                    AssetId = asset.Id,
                    AssetVersionId = version.Id,
                    State = state,
                    CaseRevision = submission.CaseRevision,
                    ActorUserId = request.ActorId,
                    Summary = state.ToString(),
                    CreatedAt = now
                });
                dbContext.OutboxMessages.Add(new OutboxMessage
                {
                    Id = decisionId,
                    Type = OutboxMessageTypes.MODERATION_DECISION,
                    Payload = JsonSerializer.Serialize(new { decisionId, submissionId = submission.Id, assetId = asset.Id, versionId = version.Id, recipientId = asset.AuthorId, revision = submission.CaseRevision, state = state.ToString(), promotion = promoted ? "PROMOTED" : "NOT_PROMOTED" }, CoverageReviewState.JsonOptions),
                    OccurredAt = now
                });
                await dbContext.SaveChangesAsync(ct);
                await MandatoryReviewAudit(AuditActions.CASE_DECIDED, request.ActorId, submission.Id, AuditOutcome.SUCCESS,
                    new { decisionId, submissionId = submission.Id, outcome = state.ToString() }, ct, false);
                var saved = new ModerationDecisionSaved(decisionId, state, snapshotId, promoted, submission.CaseRevision);
                dbContext.JsonMutationIdempotencyRecords.Add(new JsonMutationIdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = request.ActorId,
                    OperationKind = ModerationOperationKinds.CASE_DECIDE,
                    OperationId = request.OperationId,
                    RequestDigest = request.RequestDigest,
                    ResultJson = JsonSerializer.Serialize(saved, CoverageReviewState.JsonOptions),
                    CreatedAt = now
                });
                await dbContext.SaveChangesAsync(ct);
                result = new(ModerationDecisionStatus.SUCCEEDED, saved);
            }, cancellationToken);
        }
        catch (RequiredReviewAuditException) { return new(ModerationDecisionStatus.AUDIT_UNAVAILABLE); }
        catch (DbUpdateException ex) when (IsIdempotencyOperationUniqueViolation(ex)) { return new(ModerationDecisionStatus.OPERATION_CONFLICT, ReasonCode: ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT); }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.DeadlockDetected) { return new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_MODERATION_CONCURRENT_UPDATE); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.DeadlockDetected }) { return new(ModerationDecisionStatus.BLOCKED, ReasonCode: ErrorCodes.ERR_MODERATION_CONCURRENT_UPDATE); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError("Moderation decision failed exceptionType {ExceptionType}", ex.GetType().Name); throw; }
        return result;
    }
}
