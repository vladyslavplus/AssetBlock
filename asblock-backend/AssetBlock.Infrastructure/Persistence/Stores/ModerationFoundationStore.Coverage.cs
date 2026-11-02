using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed partial class ModerationFoundationStore
{
    private sealed class RequiredReviewAuditException : Exception;
    private async Task MandatoryReviewAudit(string action, Guid actorId, Guid scopeId, AuditOutcome outcome, object metadata, CancellationToken ct, bool coverageScope = true)
    {
        if (_auditWriter is null)
        { throw new RequiredReviewAuditException(); }
        Dictionary<string, object?> safe = JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(metadata))!;
        try
        { await _auditWriter.Write(new AuditEvent(action, outcome, coverageScope ? AuditResourceTypes.COVERAGE_REVIEW : AuditResourceTypes.MODERATION_SUBMISSION, scopeId.ToString(), safe, AuditActorType.USER, actorId), ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogError("Review audit failed exceptionType {ExceptionType}", ex.GetType().Name); throw new RequiredReviewAuditException(); }
    }
    private async Task<AssetDraftWorkspace?> LockReviewWorkspace(Guid workspaceId, CancellationToken ct)
    {
        Guid id = await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM asset_draft_workspaces WHERE "Id" = {workspaceId} FOR UPDATE""").FirstOrDefaultAsync(ct);
        return id == Guid.Empty ? null : await ReadWorkspaceFreshUnderLock(id, ct);
    }
    private async Task<CoverageReviewRequest?> LockCoverageRequest(Guid requestId, CancellationToken ct)
    {
        await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM coverage_review_requests WHERE "Id" = {requestId} FOR UPDATE""").ToListAsync(ct);
        CoverageReviewRequest? row = await dbContext.CoverageReviewRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == requestId, ct);
        if (row is null)
        { return null; }
        CoverageReviewRequest? tracked = dbContext.CoverageReviewRequests.Local.SingleOrDefault(r => r.Id == requestId);
        if (tracked is null)
        { dbContext.Attach(row); return row; }
        dbContext.Entry(tracked).CurrentValues.SetValues(row);
        dbContext.Entry(tracked).OriginalValues.SetValues(row);
        return tracked;
    }
    public async Task<ReviewAccessResult<CoverageReviewSaved>> RequestCoverageReview(Guid actorId, Guid assetId, Guid versionId, CoverageReviewCreateRequest input, string requestDigest, CancellationToken cancellationToken = default)
    {
        ReviewAccessResult<CoverageReviewSaved> result = new(ReviewAccessStatus.NOT_FOUND);
        var auditStarted = false;
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                Asset? asset = await assetStore.GetForUpdate(assetId, ct);
                if (asset is null || asset.DeletedAt.HasValue || asset.AuthorId != actorId)
                { result = new(ReviewAccessStatus.FORBIDDEN); return; }
                AssetDraftWorkspace? workspace = await LockReviewWorkspace(input.WorkspaceId, ct);
                if (workspace is null || workspace.AssetId != assetId)
                { return; }
                await LockActiveEvidenceCases(workspace.Id, ct);
                UserPersistedRole? role = await LockRoleAfterWorkspace(actorId, ct);
                if (!CanActAsOwner(role))
                { result = new(ReviewAccessStatus.FORBIDDEN); return; }
                JsonMutationIdempotencyRecord? receipt = await FindIdempotencyRecord(actorId, ModerationOperationKinds.COVERAGE_REQUEST, input.OperationId, ct);
                if (receipt is not null)
                { result = ReplayCoverage(receipt, requestDigest); return; }
                if (workspace.AssetVersionId != versionId || workspace.WorkspaceRevision != input.ExpectedWorkspaceRevision)
                { result = new(ReviewAccessStatus.STALE); return; }
                AssetVersion? version = await dbContext.AssetVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId && v.AssetId == assetId, ct);
                if (version is null)
                { return; }
                var blockedReason = await ReviewSubmissionEligibility.CurrentReportBlockedReason(dbContext, version, workspace, input.ReportId, ct);
                if (blockedReason is not null)
                { result = ReportBlocked(blockedReason); return; }
                CodeAnalysisReportHeader report = await dbContext.CodeAnalysisReportHeaders.SingleAsync(h => h.Id == input.ReportId, ct);
                if (report.CoverageLevel is not (CodeAnalysisCoverageLevel.PARTIAL or CodeAnalysisCoverageLevel.UNSUPPORTED))
                { result = new(ReviewAccessStatus.INVALID); return; }
                CoverageTuple? tuple = await CoverageReviewState.ReadTuple(dbContext, version, workspace, ct);
                if (tuple is null)
                { result = new(ReviewAccessStatus.STALE); return; }
                if (!CoverageReviewState.CanRequest(await CoverageReviewState.ReadStatus(dbContext, version, workspace, ct)))
                { result = new(ReviewAccessStatus.CONFLICT); return; }
                var row = new CoverageReviewRequest
                {
                    Id = Guid.NewGuid(),
                    AssetId = assetId,
                    AssetVersionId = versionId,
                    WorkspaceId = workspace.Id,
                    ReportId = report.Id,
                    RequestedWorkspaceRevision = workspace.WorkspaceRevision + 1,
                    OwnerUserId = actorId,
                    ContentSha256 = tuple.ContentSha256,
                    DeclarationRevision = tuple.DeclarationRevision,
                    MaterialRevision = tuple.MaterialRevision,
                    SellerEvidenceRevision = workspace.SellerEvidenceHeadRevision,
                    PolicyVersion = tuple.PolicyVersion,
                    PolicyDigest = tuple.PolicyDigest,
                    TupleDigest = tuple.Digest,
                    CreatedAt = _timeProvider.GetUtcNow()
                };
                dbContext.CoverageReviewRequests.Add(row);
                workspace.WorkspaceRevision++;
                var saved = new CoverageReviewSaved(row.Id, row.Revision, workspace.WorkspaceRevision, row.TupleDigest);
                await dbContext.SaveChangesAsync(ct);
                auditStarted = true;
                await MandatoryReviewAudit(AuditActions.COVERAGE_REVIEW_REQUESTED, actorId, row.Id, AuditOutcome.SUCCESS, new { requestId = row.Id, reportId = row.ReportId }, ct);
                AddCoverageReceipt(actorId, ModerationOperationKinds.COVERAGE_REQUEST, input.OperationId, requestDigest, saved);
                await dbContext.SaveChangesAsync(ct);
                result = new(ReviewAccessStatus.SUCCESS, saved);
            }, cancellationToken);
        }
        catch (RequiredReviewAuditException) { return new(ReviewAccessStatus.AUDIT_UNAVAILABLE); }
        catch (DbUpdateException ex) when (!auditStarted && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation }) { _logger.LogError("Coverage write failed exceptionType {ExceptionType}", ex.GetType().Name); return new(ReviewAccessStatus.CONFLICT); }
        catch (Exception ex) when (auditStarted && ex is not OperationCanceledException) { _logger.LogError("Coverage audit commit failed exceptionType {ExceptionType}", ex.GetType().Name); return new(ReviewAccessStatus.AUDIT_UNAVAILABLE); }
        return result;
    }
    private static ReviewAccessResult<CoverageReviewSaved> ReplayCoverage(JsonMutationIdempotencyRecord receipt, string digest) =>
        receipt.RequestDigest == digest ? new(ReviewAccessStatus.SUCCESS, JsonSerializer.Deserialize<CoverageReviewSaved>(receipt.ResultJson, CoverageReviewState.JsonOptions)! with { Replayed = true }) : new(ReviewAccessStatus.CONFLICT);
    private void AddCoverageReceipt(Guid actorId, string kind, Guid operationId, string digest, CoverageReviewSaved saved) =>
        dbContext.JsonMutationIdempotencyRecords.Add(new JsonMutationIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorId,
            OperationKind = kind,
            OperationId = operationId,
            RequestDigest = digest,
            ResultJson = JsonSerializer.Serialize(saved, CoverageReviewState.JsonOptions),
            CreatedAt = _timeProvider.GetUtcNow()
        });

    public async Task<ReviewAccessResult<CoverageReviewSaved>> AssessCoverage(Guid actorId, Guid requestId, CoverageAssessmentRequest input, string requestDigest, CancellationToken cancellationToken = default)
    {
        ReviewAccessResult<CoverageReviewSaved> result = new(ReviewAccessStatus.NOT_FOUND);
        var auditStarted = false;
        var binding = await dbContext.CoverageReviewRequests.AsNoTracking().Where(r => r.Id == requestId).Select(r => new { r.AssetId, r.WorkspaceId }).SingleOrDefaultAsync(cancellationToken);
        if (binding is null)
        { return new(AppRoles.CanReview((await userStore.GetPersistedRole(actorId, cancellationToken))?.Role) ? ReviewAccessStatus.NOT_FOUND : ReviewAccessStatus.FORBIDDEN); }
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                Asset? asset = await assetStore.GetForUpdate(binding.AssetId, ct);
                AssetDraftWorkspace? workspace = await LockReviewWorkspace(binding.WorkspaceId, ct);
                CoverageReviewRequest? request = await LockCoverageRequest(requestId, ct);
                UserPersistedRole? role = await LockRoleAfterWorkspace(actorId, ct);
                if (!AppRoles.CanReview(role?.Role) || asset is null || asset.AuthorId == actorId || request?.OwnerUserId == actorId)
                { result = new(ReviewAccessStatus.FORBIDDEN); return; }
                if (workspace is null || request is null)
                { return; }
                JsonMutationIdempotencyRecord? receipt = await FindIdempotencyRecord(actorId, ModerationOperationKinds.COVERAGE_ASSESS, input.OperationId, ct);
                if (receipt is not null)
                { result = ReplayCoverage(receipt, requestDigest); return; }
                if (!request.IsOpen || request.Revision != input.ExpectedRequestRevision || input.ExpectedTupleDigest != request.TupleDigest)
                { result = new(ReviewAccessStatus.STALE); return; }
                AssetVersion version = await dbContext.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == request.AssetVersionId, ct);
                if (asset.DeletedAt.HasValue)
                { result = new(ReviewAccessStatus.SECURITY_RESTRICTED); return; }
                var blockedReason = await ReviewSubmissionEligibility.CurrentReportBlockedReason(dbContext, version, workspace, request.ReportId, ct);
                if (blockedReason is not null)
                { result = ReportBlocked(blockedReason); return; }
                CoverageTuple? tuple = await CoverageReviewState.ReadTuple(dbContext, version, workspace, ct);
                if (tuple?.Digest != request.TupleDigest)
                { result = new(ReviewAccessStatus.STALE); return; }
                if (input.Outcome == CoverageAssessmentOutcome.ACCEPTED && (!await ScopeCanBeRead(request, input, ct) || !await HasReadCoverageEvidence(actorId, request.Id, input.Scope, ct)))
                { result = new(ReviewAccessStatus.SCOPE_UNAVAILABLE); return; }
                dbContext.CoverageAssessments.Add(new CoverageAssessment
                {
                    Id = Guid.NewGuid(),
                    RequestId = request.Id,
                    ModeratorUserId = actorId,
                    Outcome = input.Outcome,
                    CompetenceJson = JsonSerializer.Serialize(input.Competence),
                    ScopeJson = JsonSerializer.Serialize(input.Scope, CoverageReviewState.JsonOptions),
                    ManageableScope = input.ManageableScope,
                    ReviewPlan = input.ReviewPlan.Trim(),
                    CompletedPlanReference = input.CompletedPlanReference.Trim(),
                    Rationale = input.Rationale.Trim(),
                    CreatedAt = _timeProvider.GetUtcNow()
                });
                request.IsOpen = false;
                request.Revision++;
                var saved = new CoverageReviewSaved(request.Id, request.Revision, workspace.WorkspaceRevision, request.TupleDigest);
                await dbContext.SaveChangesAsync(ct);
                auditStarted = true;
                await MandatoryReviewAudit(AuditActions.COVERAGE_REVIEW_ASSESSED, actorId, request.Id, AuditOutcome.SUCCESS, new { requestId, outcome = input.Outcome.ToString() }, ct);
                AddCoverageReceipt(actorId, ModerationOperationKinds.COVERAGE_ASSESS, input.OperationId, requestDigest, saved);
                await dbContext.SaveChangesAsync(ct);
                result = new(ReviewAccessStatus.SUCCESS, saved);
            }, cancellationToken);
        }
        catch (RequiredReviewAuditException) { return new(ReviewAccessStatus.AUDIT_UNAVAILABLE); }
        catch (DbUpdateException ex) when (!auditStarted && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation or PostgresErrorCodes.CheckViolation }) { _logger.LogError("Coverage write failed exceptionType {ExceptionType}", ex.GetType().Name); return new(ReviewAccessStatus.CONFLICT); }
        catch (Exception ex) when (auditStarted && ex is not OperationCanceledException) { _logger.LogError("Coverage audit commit failed exceptionType {ExceptionType}", ex.GetType().Name); return new(ReviewAccessStatus.AUDIT_UNAVAILABLE); }
        return result;
    }
    private static ReviewAccessResult<CoverageReviewSaved> ReportBlocked(string reason) => reason == ErrorCodes.ERR_VERSION_FILE_CHECKS_INCOMPLETE
        ? new(ReviewAccessStatus.SECURITY_RESTRICTED) : new(ReviewAccessStatus.CONFLICT, ReasonCode: reason);
    private async Task<bool> HasReadCoverageEvidence(Guid actorId, Guid requestId, IReadOnlyList<CoverageScopeItem> scope, CancellationToken ct)
    {
        List<string?> metadata = await dbContext.AuditLogs.AsNoTracking().Where(a => a.ActorUserId == actorId && a.Action == AuditActions.RESTRICTED_EVIDENCE_READ
            && a.ResourceType == AuditResourceTypes.COVERAGE_REVIEW && a.ResourceId == requestId.ToString() && a.Outcome == AuditOutcome.SUCCESS).OrderByDescending(a => a.Id).Take(1000).Select(a => a.MetadataJson).ToListAsync(ct);
        var reads = new HashSet<(string Kind, Guid? FindingId)>();
        foreach (var json in metadata.Where(j => j is not null))
        {
            using var document = JsonDocument.Parse(json!);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("kind", out JsonElement kind))
            {
                Guid? findingId = root.TryGetProperty("findingId", out JsonElement finding) && finding.ValueKind == JsonValueKind.String ? finding.GetGuid() : null;
                reads.Add((kind.GetString()!, findingId));
            }
        }
        return reads.Contains((nameof(RestrictedEvidenceKind.COVERAGE_INVENTORY), null))
            && scope.All(item => reads.Contains((item.EvidenceKind.ToString(), item.FindingId)));
    }

    private async Task<JsonElement?> ExtractionData(Guid reportId, CancellationToken ct)
    {
        var json = await dbContext.CodeAnalysisReuseArtifacts.AsNoTracking().Where(a => a.SourceReportId == reportId && a.Stage == "EXTRACTION").Select(a => a.ArtifactJson).SingleOrDefaultAsync(ct);
        if (json is null)
        { return null; }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Object ? data.Clone() : null;
    }
    private async Task<bool> ScopeCanBeRead(CoverageReviewRequest request, CoverageAssessmentRequest input, CancellationToken ct)
    {
        // Competence is assessor-attested; matching labels do not independently verify expertise.
        if (!input.ManageableScope || input.Scope.Count == 0 || input.Scope.Count > ReviewEvidenceLimits.MAX_ITEMS || string.IsNullOrWhiteSpace(input.CompletedPlanReference)
            || input.Scope.Any(s => !s.Readable || !input.Competence.Contains(s.Language, StringComparer.OrdinalIgnoreCase)))
        { return false; }
        JsonElement? data = await ExtractionData(request.ReportId, ct);
        if (data is null || !data.Value.TryGetProperty("omissionOverflow", out JsonElement overflow) || !overflow.TryGetInt32(out var overflowCount) || overflowCount != 0 || !data.Value.TryGetProperty("omissions", out JsonElement omissionList) || omissionList.ValueKind != JsonValueKind.Array || !data.Value.TryGetProperty("analyzed", out JsonElement analyzed) || analyzed.ValueKind != JsonValueKind.Array)
        { return false; }
        JsonElement[] omissions = omissionList.EnumerateArray().ToArray();
        if (omissions.Length != input.Scope.Count || input.Scope.Select(s => s.Path).Distinct(StringComparer.Ordinal).Count() != input.Scope.Count)
        { return false; }
        foreach (CoverageScopeItem scope in input.Scope)
        {
            if (!omissions.Any(o => o.TryGetProperty("path", out JsonElement path) && path.GetString() == scope.Path))
            { return false; }
            if (scope.EvidenceKind == RestrictedEvidenceKind.ARCHIVE_README)
            {
                AssetArchiveAnalysis? archive = await dbContext.AssetArchiveAnalyses.AsNoTracking().SingleOrDefaultAsync(a => a.AssetVersionId == request.AssetVersionId, ct);
                if (archive?.ReadmeContent is null || omissions.Length != 1 || !new[] { "readme.md", "readme.txt", "readme.rst", "readme" }.Contains(Path.GetFileName(scope.Path).ToLowerInvariant()))
                { return false; }
                var size = Encoding.UTF8.GetByteCount(archive.ReadmeContent);
                var analyzedBytes = data.Value.GetProperty("analyzed").EnumerateArray().Sum(a => a.GetProperty("size").GetInt64());
                if (size > ReviewEvidenceLimits.MAX_TEXT_BYTES || archive.TotalExpandedBytes - analyzedBytes != size || archive.FileCount != data.Value.GetProperty("analyzed").GetArrayLength() + 1)
                { return false; }
            }
            else if (scope.EvidenceKind == RestrictedEvidenceKind.OWN_EXCERPT && scope.FindingId.HasValue)
            {
                CodeAnalysisReportFinding? finding = await dbContext.CodeAnalysisReportFindings.AsNoTracking().SingleOrDefaultAsync(f => f.Id == scope.FindingId && f.ReportId == request.ReportId && f.OwnScopePath == scope.Path, ct);
                JsonElement? file = data.Value.GetProperty("analyzed").EnumerateArray().Where(a => a.GetProperty("canonicalPath").GetString() == scope.Path).Select(a => (JsonElement?)a).FirstOrDefault();
                if (finding is null || file is null || finding.OwnStartByte != 0 || finding.OwnEndByte != Encoding.UTF8.GetByteCount(finding.OwnExcerpt) || finding.OwnEndByte != file.Value.GetProperty("size").GetInt32())
                { return false; }
            }
            else
            { return false; }
        }
        return true;
    }
    public async Task<ReviewAccessResult<CoverageQueuePage>> GetCoverageQueue(Guid actorId, int page, CancellationToken cancellationToken = default)
    {
        if (!AppRoles.CanReview((await userStore.GetPersistedRole(actorId, cancellationToken))?.Role))
        { return new(ReviewAccessStatus.FORBIDDEN); }
        if (page is < 1 or > 10000)
        { return new(ReviewAccessStatus.INVALID); }
        IQueryable<CoverageReviewRequest> query = dbContext.CoverageReviewRequests.AsNoTracking().Where(r => r.IsOpen && r.OwnerUserId != actorId
            && dbContext.Assets.Any(a => a.Id == r.AssetId && a.AuthorId != actorId && a.DeletedAt == null)
            && dbContext.AssetVersions.Any(v => v.Id == r.AssetVersionId && v.SelectedCodeAnalysisReportId == r.ReportId && v.ContentSha256 == r.ContentSha256)
            && dbContext.AssetDraftWorkspaces.Any(w => w.Id == r.WorkspaceId && w.SourceDeclarationHeadRevision == r.DeclarationRevision && w.MaterialMetadataHeadRevision == r.MaterialRevision)
            && r.PolicyVersion == ReviewAnalysisPolicy.KEY && r.PolicyDigest == ReviewAnalysisPolicy.DIGEST
            && dbContext.CodeAnalysisReportHeaders.Any(h => h.Id == r.ReportId && h.PolicyVersion == r.PolicyVersion
                && dbContext.CodeAnalysisRuns.Any(run => run.Id == h.AnalysisRunId && run.PolicyDigest == r.PolicyDigest
                    && dbContext.CodeAnalysisPolicyVersions.Any(p => p.Id == run.PolicyVersionId && p.PolicyDigest == r.PolicyDigest))));
        var count = await query.CountAsync(cancellationToken);
        CoverageQueueItem[] items = await query.OrderBy(r => r.CreatedAt).ThenBy(r => r.Id).Skip((page - 1) * ReviewEvidenceLimits.PAGE_SIZE).Take(ReviewEvidenceLimits.PAGE_SIZE)
            .Select(r => new CoverageQueueItem(r.Id, r.AssetVersionId, r.ReportId, r.Revision, r.TupleDigest, r.CreatedAt)).ToArrayAsync(cancellationToken);
        return new(ReviewAccessStatus.SUCCESS, new(items, page, ReviewEvidenceLimits.PAGE_SIZE, count, (count + ReviewEvidenceLimits.PAGE_SIZE - 1) / ReviewEvidenceLimits.PAGE_SIZE));
    }
}
