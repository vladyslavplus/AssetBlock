using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed partial class ModerationFoundationStore
{
    private async Task LockActiveEvidenceCases(Guid workspaceId, CancellationToken ct)
    {
        await dbContext.Database.SqlQuery<Guid>($"""
            SELECT "Id" AS "Value" FROM moderation_submissions WHERE "WorkspaceId" = {workspaceId}
            AND "State" IN ('SUBMITTED','IN_REVIEW') ORDER BY "Id" FOR UPDATE
            """).ToListAsync(ct);
    }

    public async Task<SellerEvidenceWriteResult> SaveSellerEvidence(SellerEvidenceWriteRequest request, CancellationToken cancellationToken = default)
    {
        var text = request.Text?.Trim();
        if (text is null || text.Length > SellerEvidenceLimits.TEXT_MAX_LENGTH || text.Contains('\0')
            || request.ResponseType.HasValue && (!Enum.IsDefined(request.ResponseType.Value)
                || request.FindingId is null || request.ResponseType != SellerResponseType.NO_EVIDENCE && text.Length == 0)
            || !request.ResponseType.HasValue && request.FindingId.HasValue)
        {
            return new(SellerEvidenceWriteStatus.INVALID_INPUT);
        }
        SellerEvidenceWriteResult outcome = null!;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            Asset? asset = await assetStore.GetForUpdate(request.AssetId, ct);
            if (asset is null || asset.AuthorId != request.ActorUserId || asset.DeletedAt.HasValue)
            {
                outcome = new(SellerEvidenceWriteStatus.FORBIDDEN);
                return;
            }
            Guid workspaceId = await dbContext.Database.SqlQuery<Guid>($"""
                SELECT "Id" AS "Value" FROM asset_draft_workspaces
                WHERE "Id" = {request.WorkspaceId} AND "AssetId" = {request.AssetId} FOR UPDATE
                """).FirstOrDefaultAsync(ct);
            AssetDraftWorkspace? workspace = workspaceId == Guid.Empty ? null : await ReadWorkspaceFreshUnderLock(workspaceId, ct);
            if (workspace is null)
            { outcome = new(SellerEvidenceWriteStatus.NOT_FOUND); return; }
            await LockActiveEvidenceCases(workspace.Id, ct);
            UserPersistedRole? role = await userStore.LockAndReadPersistedRole(request.ActorUserId, ct);
            if (!CanActAsOwner(role))
            {
                outcome = new(SellerEvidenceWriteStatus.FORBIDDEN);
                return;
            }
            var operation = request.ResponseType.HasValue ? ModerationOperationKinds.RESPONSE_SAVE : ModerationOperationKinds.EVIDENCE_SAVE;
            JsonMutationIdempotencyRecord? receipt = await FindIdempotencyRecord(request.ActorUserId, operation, request.OperationId, ct);
            if (receipt is not null)
            {
                outcome = receipt.RequestDigest == request.RequestDigest
                    ? new(SellerEvidenceWriteStatus.SAVED, JsonSerializer.Deserialize<SellerEvidenceSavedDto>(receipt.ResultJson, SellerResponseRequirements.JsonOptions)! with { Replayed = true })
                    : new(SellerEvidenceWriteStatus.OPERATION_CONFLICT);
                return;
            }
            if (workspace.AssetVersionId != request.AssetVersionId || workspace.WorkspaceVersionScopeKey != request.AssetVersionId
                || workspace.WorkspaceRevision != request.ExpectedWorkspaceRevision)
            {
                outcome = new(SellerEvidenceWriteStatus.STALE_WORKSPACE);
                return;
            }
            if (workspace.SellerEvidenceHeadRevision != request.ExpectedEvidenceRevision)
            {
                outcome = new(SellerEvidenceWriteStatus.STALE_EVIDENCE);
                return;
            }
            AssetVersion? version = await dbContext.AssetVersions.AsNoTracking()
                .SingleOrDefaultAsync(v => v.Id == request.AssetVersionId && v.AssetId == request.AssetId, ct);
            if (version is null || await ReviewSubmissionEligibility.CurrentReportBlockedReason(dbContext, version, workspace, request.ReportId, ct) is not null)
            {
                outcome = new(SellerEvidenceWriteStatus.ANALYSIS_UNAVAILABLE);
                return;
            }
            CodeAnalysisReportHeader report = await dbContext.CodeAnalysisReportHeaders.AsNoTracking().SingleAsync(h => h.Id == request.ReportId, ct);
            SellerResponseRequirementState current = await SellerResponseRequirements.Read(dbContext, report.Id, workspace, ct);
            if (!current.Available)
            { outcome = new(SellerEvidenceWriteStatus.ANALYSIS_UNAVAILABLE); return; }
            if (request.FindingId is { } findingId && !current.Required.Contains(findingId))
            {
                outcome = new(SellerEvidenceWriteStatus.INVALID_FINDING);
                return;
            }
            DateTimeOffset now = _timeProvider.GetUtcNow();
            var revision = workspace.SellerEvidenceHeadRevision + 1;
            var answers = current.Answers.Values.Where(r => r.FindingId != request.FindingId).Select(r => r.Id).ToList();
            Guid? responseId = null;
            if (request.ResponseType is { } responseType)
            {
                responseId = Guid.NewGuid();
                answers.Add(responseId.Value);
                dbContext.SellerResponses.Add(new SellerResponse
                {
                    Id = responseId.Value,
                    AssetId = asset.Id,
                    AssetVersionId = version.Id,
                    WorkspaceId = workspace.Id,
                    WorkspaceVersionScopeKey = version.Id,
                    ReportId = report.Id,
                    FindingId = request.FindingId!.Value,
                    SellerEvidenceRevision = revision,
                    ResponseType = responseType,
                    Text = text,
                    ActorUserId = request.ActorUserId,
                    CreatedAt = now
                });
            }
            var snapshot = new SellerEvidenceSnapshot(report.Id, answers.Order().ToArray(), request.ResponseType.HasValue ? current.Snapshot.Text : text);
            var payload = JsonSerializer.Serialize(snapshot, SellerResponseRequirements.JsonOptions);
            await SupersedeActiveCase(workspace, request.ActorUserId, now, ct);
            workspace.SellerEvidenceHeadRevision = revision;
            workspace.WorkspaceRevision++;
            dbContext.AssetSellerEvidenceRevisions.Add(new AssetSellerEvidenceRevision
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                AssetVersionId = version.Id,
                WorkspaceId = workspace.Id,
                WorkspaceVersionScopeKey = version.Id,
                Revision = revision,
                AuthorUserId = request.ActorUserId,
                ContentDigest = ComputeSha256Hex(payload),
                SchemaVersion = 1,
                PayloadJson = payload,
                CreatedAt = now
            });
            var saved = new SellerEvidenceSavedDto(responseId, workspace.Id, workspace.WorkspaceRevision, revision, workspace.CaseRevision);
            await WriteAuditInTransaction(new AuditEvent(request.ResponseType.HasValue ? AuditActions.SELLER_RESPONSE_SAVED : AuditActions.SELLER_EVIDENCE_SAVED,
                AuditOutcome.SUCCESS, AuditResourceTypes.ASSET_VERSION, version.Id.ToString(),
                new Dictionary<string, object?> { ["responseId"] = responseId, ["responseType"] = request.ResponseType?.ToString(), ["evidenceRevision"] = revision },
                AuditActorType.USER, request.ActorUserId), ct);
            dbContext.JsonMutationIdempotencyRecords.Add(new JsonMutationIdempotencyRecord
            {
                Id = Guid.NewGuid(),
                ActorUserId = request.ActorUserId,
                OperationKind = operation,
                OperationId = request.OperationId,
                RequestDigest = request.RequestDigest,
                ResultJson = JsonSerializer.Serialize(saved, SellerResponseRequirements.JsonOptions),
                CreatedAt = now
            });
            await dbContext.SaveChangesAsync(ct);
            outcome = new(SellerEvidenceWriteStatus.SAVED, saved);
        }, cancellationToken);
        return outcome;
    }

    public async Task<SellerResponsesPageDto?> GetOwnerResponses(Guid assetId, Guid assetVersionId, Guid ownerUserId, int page, CancellationToken cancellationToken = default)
    {
        if (page < 1 || page > 10000)
        { return null; }
        if (!await dbContext.Assets.AnyAsync(a => a.Id == assetId && a.AuthorId == ownerUserId && a.DeletedAt == null, cancellationToken))
        { return null; }
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        AssetDraftWorkspace? workspace = await dbContext.AssetDraftWorkspaces.AsNoTracking()
            .SingleOrDefaultAsync(w => w.AssetId == assetId && w.AssetVersionId == assetVersionId, cancellationToken);
        AssetVersion? version = await dbContext.AssetVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == assetVersionId && v.AssetId == assetId && v.Asset.AuthorId == ownerUserId && v.Asset.DeletedAt == null, cancellationToken);
        if (workspace is null || version is null)
        { return null; }
        SellerResponseRequirementState? current = version.SelectedCodeAnalysisReportId is { } reportId
            ? await SellerResponseRequirements.Read(dbContext, reportId, workspace, cancellationToken) : null;
        IQueryable<SellerResponse> query = dbContext.SellerResponses.AsNoTracking().Where(r => r.AssetVersionId == assetVersionId && r.AssetId == assetId);
        var total = await query.CountAsync(cancellationToken);
        List<SellerResponse> rows = await query.OrderByDescending(r => r.SellerEvidenceRevision).ThenByDescending(r => r.Id)
            .Skip((page - 1) * SellerEvidenceLimits.PAGE_SIZE).Take(SellerEvidenceLimits.PAGE_SIZE).ToListAsync(cancellationToken);
        HashSet<Guid> active = current?.Answers.Values.Select(r => r.Id).ToHashSet() ?? [];
        SellerResponseDto[] items = rows.Select(r => new SellerResponseDto(r.Id, r.AssetVersionId, r.ReportId, r.FindingId,
            r.SellerEvidenceRevision, r.ResponseType, r.Text, r.CreatedAt, r.ActorUserId, active.Contains(r.Id))).ToArray();
        await transaction.CommitAsync(cancellationToken);
        return new(workspace.Id, workspace.WorkspaceRevision, workspace.CaseRevision, workspace.SellerEvidenceHeadRevision,
            version.SelectedCodeAnalysisReportId, current?.Required.Count ?? 0, current?.Outstanding ?? 0, current?.Snapshot.Text ?? "",
            items, total, page, SellerEvidenceLimits.PAGE_SIZE, (total + SellerEvidenceLimits.PAGE_SIZE - 1) / SellerEvidenceLimits.PAGE_SIZE);
    }

    public async Task<bool> AppendFindingDispositions(Guid submissionId, Guid moderatorUserId, long expectedCaseRevision,
        IReadOnlyList<FindingDispositionInput> dispositions, CancellationToken cancellationToken = default)
    {
        if (dbContext.Database.CurrentTransaction is null)
        { throw new InvalidOperationException("Disposition writes require the caller transaction."); }
        if (dispositions.Count == 0 || dispositions.Count > SellerEvidenceLimits.MAX_RESPONSES
            || dispositions.Select(d => d.FindingId).Distinct().Count() != dispositions.Count
            || dispositions.Any(d => !Enum.IsDefined(d.Disposition) || string.IsNullOrWhiteSpace(d.Rationale)
                || d.Rationale.Trim().Length > SellerEvidenceLimits.TEXT_MAX_LENGTH || d.Rationale.Contains('\0')))
        { return false; }
        var binding = await dbContext.ModerationSubmissions.AsNoTracking().Where(s => s.Id == submissionId)
            .Select(s => new { s.AssetId, s.WorkspaceId }).SingleOrDefaultAsync(cancellationToken);
        if (binding is null)
        { return false; }
        Asset? asset = await assetStore.GetForUpdate(binding.AssetId, cancellationToken);
        Guid workspaceId = await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM asset_draft_workspaces WHERE "Id" = {binding.WorkspaceId} FOR UPDATE""").FirstOrDefaultAsync(cancellationToken);
        await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM moderation_submissions WHERE "Id" = {submissionId} FOR UPDATE""").ToListAsync(cancellationToken);
        UserPersistedRole? role = await userStore.LockAndReadPersistedRole(moderatorUserId, cancellationToken);
        ModerationSubmission? submission = await dbContext.ModerationSubmissions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (asset is null || asset.DeletedAt.HasValue || asset.AuthorId == moderatorUserId || role?.Role != AppRoles.MODERATOR || workspaceId == Guid.Empty
            || submission is null || submission.CaseRevision != expectedCaseRevision || submission.State is not (ModerationSubmissionState.SUBMITTED or ModerationSubmissionState.IN_REVIEW)
            || submission.CodeAnalysisReportHeaderId is null)
        { return false; }
        Guid[] ids = dispositions.Select(d => d.FindingId).ToArray();
        if (await dbContext.CodeAnalysisReportFindings.CountAsync(f => ids.Contains(f.Id) && f.ReportId == submission.CodeAnalysisReportHeaderId, cancellationToken) != ids.Length)
        { return false; }
        foreach (FindingDispositionInput input in dispositions)
        {
            dbContext.ModerationFindingDispositions.Add(new ModerationFindingDisposition
            {
                Id = Guid.NewGuid(),
                AssetId = submission.AssetId,
                AssetVersionId = submission.AssetVersionId,
                SubmissionId = submission.Id,
                ReportId = submission.CodeAnalysisReportHeaderId.Value,
                FindingId = input.FindingId,
                ModeratorUserId = moderatorUserId,
                CaseRevision = expectedCaseRevision,
                Disposition = input.Disposition,
                Rationale = input.Rationale.Trim(),
                CreatedAt = _timeProvider.GetUtcNow()
            });
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
    }
}
