using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed partial class ModerationFoundationStore
{
    private async Task<Asset?> ShareReviewAsset(Guid id, CancellationToken ct)
    {
        await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM assets WHERE "Id" = {id} FOR SHARE""").ToListAsync(ct);
        return await dbContext.Assets.AsNoTracking().SingleOrDefaultAsync(a => a.Id == id, ct);
    }
    private async Task<AssetDraftWorkspace?> ShareReviewWorkspace(Guid id, CancellationToken ct)
    {
        await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM asset_draft_workspaces WHERE "Id" = {id} FOR SHARE""").ToListAsync(ct);
        return await dbContext.AssetDraftWorkspaces.AsNoTracking().SingleOrDefaultAsync(w => w.Id == id, ct);
    }
    private async Task<UserPersistedRole?> ShareReviewRole(Guid id, CancellationToken ct)
    {
        await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM users WHERE "Id" = {id} FOR SHARE""").ToListAsync(ct);
        return await userStore.GetPersistedRole(id, ct);
    }
    public async Task<ModerationCaseAccessResult> GetCaseSummaryForModerator(Guid moderatorUserId, Guid submissionId, CancellationToken cancellationToken = default)
    {
        if (!AppRoles.CanReview((await userStore.GetPersistedRole(moderatorUserId, cancellationToken))?.Role))
        { return new(ModerationCaseAccessStatus.DENIED_ROLE, null); }
        var binding = await dbContext.ModerationSubmissions.AsNoTracking().Where(s => s.Id == submissionId)
            .Select(s => new { s.AssetId, s.AssetVersionId, s.WorkspaceId, s.OwnerUserId, s.Kind }).SingleOrDefaultAsync(cancellationToken);
        if (binding is null)
        { return new(ModerationCaseAccessStatus.NOT_FOUND, null); }
        if (dbContext.Database.CurrentTransaction is not null)
        { return new(ModerationCaseAccessStatus.AUDIT_UNAVAILABLE, null); }
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            Asset? asset = await ShareReviewAsset(binding.AssetId, cancellationToken);
            await ShareReviewWorkspace(binding.WorkspaceId, cancellationToken);
            await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM moderation_submissions WHERE "Id" = {submissionId} FOR SHARE""").ToListAsync(cancellationToken);
            UserPersistedRole? role = await ShareReviewRole(moderatorUserId, cancellationToken);
            if (!AppRoles.CanReview(role?.Role))
            { return new(ModerationCaseAccessStatus.DENIED_ROLE, null); }
            if (binding.Kind == ModerationSubmissionKind.APPEAL && await AppealReviewExclusions.ForSubmission(dbContext, submissionId).ContainsAsync(moderatorUserId, cancellationToken))
            { return new(ModerationCaseAccessStatus.APPEAL_REVIEWER_EXCLUDED, null); }
            if (asset?.AuthorId == moderatorUserId || binding.OwnerUserId == moderatorUserId)
            { return new(ModerationCaseAccessStatus.SELF_OWNED_DENIED, null); }
            AssetVersion version = await dbContext.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == binding.AssetVersionId, cancellationToken);
            if (asset is null || asset.DeletedAt.HasValue || version.SafetyRestriction != AssetVersionSafetyRestriction.NONE || version.ProcessingStatus != AssetVersionProcessingStatus.READY)
            { return new(ModerationCaseAccessStatus.SECURITY_RESTRICTED, null); }
            ModerationCaseAccessResult result = await ReadCaseSummaryUnderLocks(moderatorUserId, submissionId, cancellationToken);
            if (result.Status != ModerationCaseAccessStatus.FOUND)
            { return result; }
            await MandatoryReviewAudit(AuditActions.RESTRICTED_EVIDENCE_READ, moderatorUserId, submissionId, AuditOutcome.SUCCESS,
                new { scopeId = submissionId, kind = "CASE_SUMMARY", outcome = "SUCCESS" }, cancellationToken, false);
            await dbContext.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return result;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _logger.LogError("Review read failed exceptionType {ExceptionType}", ex.GetType().Name); await tx.RollbackAsync(CancellationToken.None); return new(ModerationCaseAccessStatus.AUDIT_UNAVAILABLE, null); }
    }

    private sealed record EvidenceBinding(Guid AssetId, Guid VersionId, Guid WorkspaceId, Guid? ReportId, int DeclarationRevision, int MaterialRevision, int EvidenceRevision, Guid OwnerId, string ContentSha256, ModerationSubmissionKind? SubmissionKind);
    public async Task<ReviewAccessResult<RestrictedEvidenceDto>> ReadRestrictedEvidence(Guid actorId, Guid scopeId, bool coverageScope, RestrictedEvidenceKind kind, Guid? findingId, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(kind))
        { return new(ReviewAccessStatus.INVALID); }
        if (!AppRoles.CanReview((await userStore.GetPersistedRole(actorId, cancellationToken))?.Role))
        { return new(ReviewAccessStatus.FORBIDDEN); }
        if (dbContext.Database.CurrentTransaction is not null)
        { return new(ReviewAccessStatus.AUDIT_UNAVAILABLE); }
        EvidenceBinding? binding = coverageScope
            ? await dbContext.CoverageReviewRequests.AsNoTracking().Where(r => r.Id == scopeId).Select(r => new EvidenceBinding(r.AssetId, r.AssetVersionId, r.WorkspaceId, r.ReportId, r.DeclarationRevision, r.MaterialRevision, r.SellerEvidenceRevision, r.OwnerUserId, r.ContentSha256, null)).SingleOrDefaultAsync(cancellationToken)
            : await dbContext.ModerationSubmissions.AsNoTracking().Where(s => s.Id == scopeId).Select(s => new EvidenceBinding(s.AssetId, s.AssetVersionId, s.WorkspaceId, s.CodeAnalysisReportHeaderId, s.DeclarationRevision, s.MaterialMetadataRevision, s.SellerEvidenceRevision, s.OwnerUserId, s.ContentSha256, s.Kind)).SingleOrDefaultAsync(cancellationToken);
        if (binding is null)
        { return new(ReviewAccessStatus.NOT_FOUND); }
        // This method owns the commit boundary: no lazy enumeration/stream can escape before audit commits.
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            Asset? asset = await ShareReviewAsset(binding.AssetId, cancellationToken);
            AssetDraftWorkspace? workspace = await ShareReviewWorkspace(binding.WorkspaceId, cancellationToken);
            if (coverageScope)
            { await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM coverage_review_requests WHERE "Id" = {scopeId} FOR SHARE""").ToListAsync(cancellationToken); }
            else
            { await dbContext.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM moderation_submissions WHERE "Id" = {scopeId} FOR SHARE""").ToListAsync(cancellationToken); }
            UserPersistedRole? role = await ShareReviewRole(actorId, cancellationToken);
            var excluded = AppRoles.CanReview(role?.Role) && binding.SubmissionKind == ModerationSubmissionKind.APPEAL
                && await AppealReviewExclusions.ForSubmission(dbContext, scopeId).ContainsAsync(actorId, cancellationToken);
            ReviewAccessStatus status = !AppRoles.CanReview(role?.Role) || excluded || asset?.AuthorId == actorId || binding.OwnerId == actorId
                ? ReviewAccessStatus.FORBIDDEN : ReviewAccessStatus.SUCCESS;
            AssetVersion? version = await dbContext.AssetVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == binding.VersionId && v.AssetId == binding.AssetId, cancellationToken);
            if (status == ReviewAccessStatus.SUCCESS && (asset is null || workspace is null || version is null || version.ContentSha256 != binding.ContentSha256))
            { status = ReviewAccessStatus.NOT_FOUND; }
            if (status == ReviewAccessStatus.SUCCESS && (asset!.DeletedAt.HasValue || version!.ProcessingStatus != AssetVersionProcessingStatus.READY || version.SafetyRestriction != AssetVersionSafetyRestriction.NONE))
            { status = ReviewAccessStatus.SECURITY_RESTRICTED; }
            (string Text, int Items, bool Overflow)? payload = status == ReviewAccessStatus.SUCCESS ? await LoadEvidence(binding, kind, findingId, cancellationToken) : null;
            if (status == ReviewAccessStatus.SUCCESS && payload is null)
            { status = ReviewAccessStatus.NOT_FOUND; }
            RestrictedEvidenceDto? dto = null;
            if (payload is { } value)
            {
                (string Text, bool Truncated) bounded = BoundEvidenceText(value.Text, value.Overflow);
                dto = new(scopeId, binding.ReportId!.Value, kind, findingId, bounded.Text, value.Items, bounded.Truncated);
                if (JsonSerializer.SerializeToUtf8Bytes(dto, CoverageReviewState.JsonOptions).Length > ReviewEvidenceLimits.MAX_RESPONSE_BYTES)
                { dto = null; status = ReviewAccessStatus.SCOPE_UNAVAILABLE; }
            }
            await MandatoryReviewAudit(AuditActions.RESTRICTED_EVIDENCE_READ, actorId, scopeId, status == ReviewAccessStatus.SUCCESS ? AuditOutcome.SUCCESS : AuditOutcome.DENIED,
                new { scopeId, coverageScope, kind = kind.ToString(), findingId, outcome = status.ToString() }, cancellationToken, coverageScope);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(status, status == ReviewAccessStatus.SUCCESS ? dto : null, excluded ? ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED : null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogError("Review read failed exceptionType {ExceptionType}", ex.GetType().Name);
            await transaction.RollbackAsync(CancellationToken.None);
            // Mandatory access auditing and its commit are fail-closed. Do not expose private query/error details.
            return new(ReviewAccessStatus.AUDIT_UNAVAILABLE);
        }
    }
    private async Task<(string Text, int Items, bool Overflow)?> LoadEvidence(EvidenceBinding scope, RestrictedEvidenceKind kind, Guid? findingId, CancellationToken ct)
    {
        if (scope.ReportId is null || !await dbContext.CodeAnalysisReportHeaders.AnyAsync(h => h.Id == scope.ReportId && h.AssetVersionId == scope.VersionId && h.AssetId == scope.AssetId && h.ContentSha256 == scope.ContentSha256, ct))
        { return null; }
        if (kind is RestrictedEvidenceKind.OWN_EXCERPT or RestrictedEvidenceKind.FINDING_PROVENANCE)
        {
            if (findingId is null)
            { return null; }
            CodeAnalysisReportFinding? finding = await dbContext.CodeAnalysisReportFindings.AsNoTracking().SingleOrDefaultAsync(f => f.Id == findingId && f.ReportId == scope.ReportId, ct);
            if (finding is null)
            { return null; }
            return (kind == RestrictedEvidenceKind.OWN_EXCERPT ? finding.OwnExcerpt : JsonSerializer.Serialize(new
            {
                finding.FindingKey,
                finding.OwnScopePath,
                finding.OwnStartByte,
                finding.OwnEndByte,
                finding.ForeignSourceId,
                finding.ForeignOrigin,
                finding.ForeignSensitivity,
                finding.PublicAttributionJson,
                finding.RetrievalEvidenceJson,
                finding.ReasonText,
                finding.RequestedAction
            }, CoverageReviewState.JsonOptions), 1, false);
        }
        if (findingId is not null)
        { return null; }
        string? text;
        switch (kind)
        {
            case RestrictedEvidenceKind.DECLARATION:
                text = await dbContext.AssetSourceDeclarationRevisions.Where(r => r.WorkspaceId == scope.WorkspaceId && r.Revision == scope.DeclarationRevision).Select(r => r.PayloadJson).SingleOrDefaultAsync(ct);
                break;
            case RestrictedEvidenceKind.MATERIAL_METADATA:
                text = await dbContext.AssetMaterialMetadataRevisions.Where(r => r.WorkspaceId == scope.WorkspaceId && r.Revision == scope.MaterialRevision).Select(r => r.PayloadJson).SingleOrDefaultAsync(ct);
                break;
            case RestrictedEvidenceKind.ARCHIVE_README:
                text = await dbContext.AssetArchiveAnalyses.Where(a => a.AssetVersionId == scope.VersionId).Select(a => a.ReadmeContent).SingleOrDefaultAsync(ct);
                break;
            case RestrictedEvidenceKind.COVERAGE_INVENTORY:
                JsonElement? data = await ExtractionData(scope.ReportId.Value, ct);
                if (data is null || !data.Value.TryGetProperty("omissions", out JsonElement omissions) || omissions.ValueKind != JsonValueKind.Array || !data.Value.TryGetProperty("omissionOverflow", out JsonElement overflow) || !overflow.TryGetInt32(out var overflowCount) || !data.Value.TryGetProperty("analyzed", out JsonElement analyzed) || analyzed.ValueKind != JsonValueKind.Array || !data.Value.TryGetProperty("coverageByDialect", out JsonElement dialect))
                { return null; }
                JsonElement[] omitted = omissions.EnumerateArray().Take(ReviewEvidenceLimits.MAX_ITEMS).Select(o => o.Clone()).ToArray();
                return (JsonSerializer.Serialize(new { omissions = omitted, omissionOverflow = overflowCount, analyzedCount = analyzed.GetArrayLength(), coverageByDialect = dialect }, CoverageReviewState.JsonOptions), omitted.Length,
                    omissions.GetArrayLength() > omitted.Length || overflowCount != 0);
            case RestrictedEvidenceKind.SELLER_EVIDENCE:
                if (scope.EvidenceRevision == 0)
                { return ("No seller evidence was supplied.", 0, false); }
                var json = await dbContext.AssetSellerEvidenceRevisions.Where(e => e.WorkspaceId == scope.WorkspaceId && e.Revision == scope.EvidenceRevision).Select(e => e.PayloadJson).SingleOrDefaultAsync(ct);
                if (json is null)
                { return null; }
                SellerEvidenceSnapshot snapshot = JsonSerializer.Deserialize<SellerEvidenceSnapshot>(json, SellerResponseRequirements.JsonOptions)!;
                if (snapshot.ReportId != scope.ReportId)
                { return ("No seller answers are bound to this report.", 0, false); }
                Guid[] ids = snapshot.ResponseIds.Order().Take(ReviewEvidenceLimits.MAX_ITEMS).ToArray();
                var answers = await dbContext.SellerResponses.AsNoTracking().Where(r => ids.Contains(r.Id) && r.ReportId == scope.ReportId && r.WorkspaceId == scope.WorkspaceId)
                    .OrderBy(r => r.FindingId).Select(r => new { r.Id, r.FindingId, r.ResponseType, r.Text }).ToArrayAsync(ct);
                return (JsonSerializer.Serialize(new { snapshot.Text, answers }, CoverageReviewState.JsonOptions), answers.Length, snapshot.ResponseIds.Count > ids.Length);
            default:
                return null;
        }
        return text is null ? null : (text, 1, false);
    }
    internal static (string Text, bool Truncated) BoundEvidenceText(string value, bool overflow)
    {
        if (!overflow && Encoding.UTF8.GetByteCount(value) <= ReviewEvidenceLimits.MAX_TEXT_BYTES)
        { return (value, false); }
        var builder = new StringBuilder();
        var used = 0;
        var limit = ReviewEvidenceLimits.MAX_TEXT_BYTES - Encoding.UTF8.GetByteCount(ReviewEvidenceLimits.TRUNCATION_MARKER);
        foreach (Rune rune in value.EnumerateRunes())
        {
            if (used + rune.Utf8SequenceLength > limit)
            { break; }
            builder.Append(rune.ToString());
            used += rune.Utf8SequenceLength;
        }
        return (builder + ReviewEvidenceLimits.TRUNCATION_MARKER, true);
    }
}
