using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed partial class ModerationFoundationStore
{
    public async Task<CaseClaimResult> ChangeCaseClaim(CaseClaimChange request, CancellationToken cancellationToken = default)
    {
        var binding = await dbContext.ModerationSubmissions.AsNoTracking().Where(s => s.Id == request.SubmissionId)
            .Select(s => new { s.AssetId, s.WorkspaceId }).SingleOrDefaultAsync(cancellationToken);
        if (binding is null)
        {
            return new(AppRoles.CanReview((await userStore.GetPersistedRole(request.ActorId, cancellationToken))?.Role) ? CaseClaimStatus.NOT_FOUND : CaseClaimStatus.FORBIDDEN);
        }
        CaseClaimResult result = new(CaseClaimStatus.NOT_FOUND);
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                Asset? asset = await assetStore.GetForUpdate(binding.AssetId, ct);
                AssetDraftWorkspace? workspace = await LockReviewWorkspace(binding.WorkspaceId, ct);
                Guid locked = await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM moderation_submissions WHERE "Id" = {request.SubmissionId} FOR UPDATE""").FirstOrDefaultAsync(ct);
                ModerationSubmission? submission = locked == Guid.Empty ? null : await ReadSubmissionFreshUnderLock(locked, ct);
                UserPersistedRole? role;
                UserPersistedRole? holderRole = null;
                if (submission?.ClaimHolderId is Guid priorHolder && priorHolder != request.ActorId)
                {
                    (UserPersistedRole? first, UserPersistedRole? second) = await userStore.LockUsersForRoleUpdateInOrder(request.ActorId, priorHolder, ct);
                    role = first?.UserId == request.ActorId ? first : second;
                    holderRole = first?.UserId == priorHolder ? first : second;
                }
                else
                { role = await LockRoleAfterWorkspace(request.ActorId, ct); }
                if (!AppRoles.CanReview(role?.Role))
                { result = new(CaseClaimStatus.FORBIDDEN); return; }
                if (asset is null || asset.DeletedAt.HasValue || workspace is null || submission is null)
                { return; }
                if (request.Claim && submission.Kind == ModerationSubmissionKind.APPEAL && await AppealReviewExclusions.ForSubmission(dbContext, submission.Id).ContainsAsync(request.ActorId, ct))
                { result = new(CaseClaimStatus.APPEAL_REVIEWER_EXCLUDED); return; }
                var isOwner = asset.AuthorId == request.ActorId || submission.OwnerUserId == request.ActorId;
                if (isOwner && (request.Claim || submission.Kind != ModerationSubmissionKind.APPEAL))
                { result = new(CaseClaimStatus.FORBIDDEN); return; }
                var operation = request.Claim ? ModerationOperationKinds.CASE_CLAIM : ModerationOperationKinds.CASE_RELEASE;
                JsonMutationIdempotencyRecord? receipt = await FindIdempotencyRecord(request.ActorId, operation, request.OperationId, ct);
                if (receipt is not null)
                {
                    result = receipt.RequestDigest == request.Digest
                        ? new(CaseClaimStatus.SUCCESS, JsonSerializer.Deserialize<CaseClaimSaved>(receipt.ResultJson, CoverageReviewState.JsonOptions)! with { Replayed = true })
                        : new(CaseClaimStatus.OPERATION_CONFLICT);
                    return;
                }
                if (isOwner && submission.ClaimHolderId != request.ActorId)
                { result = new(CaseClaimStatus.FORBIDDEN); return; }
                if (submission.State is not (ModerationSubmissionState.SUBMITTED or ModerationSubmissionState.IN_REVIEW))
                { result = new(CaseClaimStatus.NOT_ACTIVE); return; }
                if (request.Claim && submission.State == ModerationSubmissionState.IN_REVIEW && submission.ClaimHolderId == request.ActorId)
                {
                    var existing = new CaseClaimSaved(submission.Id, submission.State, submission.ClaimHolderId, submission.CaseRevision);
                    dbContext.JsonMutationIdempotencyRecords.Add(new JsonMutationIdempotencyRecord
                    {
                        Id = Guid.NewGuid(),
                        ActorUserId = request.ActorId,
                        OperationKind = operation,
                        OperationId = request.OperationId,
                        RequestDigest = request.Digest,
                        ResultJson = JsonSerializer.Serialize(existing, CoverageReviewState.JsonOptions),
                        CreatedAt = _timeProvider.GetUtcNow()
                    });
                    await dbContext.SaveChangesAsync(ct);
                    result = new(CaseClaimStatus.SUCCESS, existing);
                    return;
                }
                if (!request.Claim && submission.State != ModerationSubmissionState.IN_REVIEW)
                { result = new(CaseClaimStatus.NOT_CLAIMED); return; }
                if (!request.Claim && submission.ClaimHolderId != request.ActorId)
                { result = new(CaseClaimStatus.NOT_HOLDER); return; }
                var takeover = false;
                if (request.Claim && submission.State == ModerationSubmissionState.IN_REVIEW && submission.ClaimHolderId is Guid holder)
                {
                    if (AppRoles.CanReview(holderRole?.Role) && holder != asset.AuthorId && holder != submission.OwnerUserId
                        && (submission.Kind != ModerationSubmissionKind.APPEAL || !await AppealReviewExclusions.ForSubmission(dbContext, submission.Id).ContainsAsync(holder, ct)))
                    { result = new(CaseClaimStatus.HELD); return; }
                    takeover = true;
                }
                if (submission.CaseRevision != request.ExpectedCaseRevision)
                { result = new(CaseClaimStatus.STALE); return; }
                DateTimeOffset now = _timeProvider.GetUtcNow();
                submission.State = request.Claim ? ModerationSubmissionState.IN_REVIEW : ModerationSubmissionState.SUBMITTED;
                submission.ClaimHolderId = request.Claim ? request.ActorId : null;
                submission.ClaimedAt = request.Claim ? now : null;
                submission.CaseRevision++;
                workspace.CaseRevision++;
                if (request.Claim)
                { EnqueueLifecycleNotification(submission, NotificationKind.MODERATION_REVIEW_STARTED, null, now); }
                dbContext.ModerationSubmissionHistoryEntries.Add(new ModerationSubmissionHistoryEntry
                {
                    Id = Guid.NewGuid(),
                    SubmissionId = submission.Id,
                    AssetId = submission.AssetId,
                    AssetVersionId = submission.AssetVersionId,
                    State = submission.State,
                    CaseRevision = submission.CaseRevision,
                    ActorUserId = request.ActorId,
                    Summary = takeover ? "Claim taken over after reviewer became unavailable." : request.Claim ? "Claimed for review." : "Released from review.",
                    CreatedAt = now
                });
                var saved = new CaseClaimSaved(submission.Id, submission.State, submission.ClaimHolderId, submission.CaseRevision);
                await dbContext.SaveChangesAsync(ct);
                await MandatoryReviewAudit(request.Claim ? AuditActions.CASE_CLAIMED : AuditActions.CASE_RELEASED, request.ActorId, submission.Id,
                    AuditOutcome.SUCCESS, new { submissionId = submission.Id, state = submission.State.ToString(), transition = takeover ? "TAKEOVER" : request.Claim ? "CLAIM" : "RELEASE", previousHolderId = takeover ? holderRole?.UserId : null }, ct, false);
                dbContext.JsonMutationIdempotencyRecords.Add(new JsonMutationIdempotencyRecord
                {
                    Id = Guid.NewGuid(),
                    ActorUserId = request.ActorId,
                    OperationKind = operation,
                    OperationId = request.OperationId,
                    RequestDigest = request.Digest,
                    ResultJson = JsonSerializer.Serialize(saved, CoverageReviewState.JsonOptions),
                    CreatedAt = now
                });
                await dbContext.SaveChangesAsync(ct);
                result = new(CaseClaimStatus.SUCCESS, saved);
            }, cancellationToken);
        }
        catch (RequiredReviewAuditException) { return new(CaseClaimStatus.AUDIT_UNAVAILABLE); }
        catch (DbUpdateException ex) when (IsIdempotencyOperationUniqueViolation(ex)) { return new(CaseClaimStatus.OPERATION_CONFLICT); }
        catch (Exception ex) when (ex is not OperationCanceledException) { _logger.LogError("Case claim failed exceptionType {ExceptionType}", ex.GetType().Name); throw; }
        return result;
    }
}
