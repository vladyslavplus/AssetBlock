using System.Security.Cryptography;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed partial class ModerationFoundationStore
{
    private const string QUEUE_CURSOR_PURPOSE = "moderation-queue-cursor";
    private static readonly TimeSpan QUEUE_CURSOR_LIFETIME = TimeSpan.FromHours(1);
    private sealed record QueueCursor(Guid ActorId, ModerationSubmissionState? State, CaseClaimFilter ClaimFilter, int PageSize, DateTimeOffset CreatedAt, Guid Id, DateTimeOffset ExpiresAt);

    public async Task<ReviewAccessResult<ModeratorQueuePage>> GetModeratorQueue(ModeratorQueueRequest request, CancellationToken cancellationToken = default)
    {
        if (!AppRoles.CanReview((await userStore.GetPersistedRole(request.ActorId, cancellationToken))?.Role))
        { return new(ReviewAccessStatus.FORBIDDEN); }
        if (request.PageSize is < 1 or > 100 || !Enum.IsDefined(request.ClaimFilter) || request.State is not (null or ModerationSubmissionState.SUBMITTED or ModerationSubmissionState.IN_REVIEW) || request.Cursor?.Length > 2048)
        { return new(ReviewAccessStatus.INVALID); }
        if (_dataProtectionProvider is null)
        { throw new InvalidOperationException("Queue cursor protection is unavailable."); }
        ITimeLimitedDataProtector protector = _dataProtectionProvider.CreateProtector(QUEUE_CURSOR_PURPOSE).ToTimeLimitedDataProtector();
        QueueCursor? cursor = null;
        if (request.Cursor is not null)
        {
            try
            { cursor = JsonSerializer.Deserialize<QueueCursor>(protector.Unprotect(request.Cursor), CoverageReviewState.JsonOptions); }
            catch (Exception ex) when (ex is CryptographicException or JsonException or FormatException) { return new(ReviewAccessStatus.INVALID); }
            if (cursor is null || cursor.ExpiresAt <= _timeProvider.GetUtcNow() || cursor.ActorId != request.ActorId || cursor.State != request.State || cursor.ClaimFilter != request.ClaimFilter || cursor.PageSize != request.PageSize)
            { return new(ReviewAccessStatus.INVALID); }
        }
        IQueryable<User> reviewers = dbContext.Users.Where(AppRoles.CanReviewUserExpression);
        IQueryable<AppealReviewExclusion> exclusions = AppealReviewExclusions.All(dbContext);
        IQueryable<Guid> excluded = exclusions.Where(x => x.UserId == request.ActorId).Select(x => x.SubmissionId);
        IQueryable<ModerationSubmission> query = dbContext.ModerationSubmissions.AsNoTracking()
            .Where(s => (s.State == ModerationSubmissionState.SUBMITTED || s.State == ModerationSubmissionState.IN_REVIEW)
                && s.OwnerUserId != request.ActorId && s.Asset.AuthorId != request.ActorId && s.Asset.DeletedAt == null
                && !excluded.Contains(s.Id));
        if (request.State == ModerationSubmissionState.SUBMITTED)
        {
            query = query.Where(s => s.State == ModerationSubmissionState.SUBMITTED || !reviewers.Any(u => u.Id == s.ClaimHolderId && u.Id != s.OwnerUserId && u.Id != s.Asset.AuthorId && !exclusions.Any(e => e.SubmissionId == s.Id && e.UserId == u.Id)));
        }
        if (request.State == ModerationSubmissionState.IN_REVIEW)
        {
            query = query.Where(s => s.State == ModerationSubmissionState.IN_REVIEW && reviewers.Any(u => u.Id == s.ClaimHolderId && u.Id != s.OwnerUserId && u.Id != s.Asset.AuthorId && !exclusions.Any(e => e.SubmissionId == s.Id && e.UserId == u.Id)));
        }
        if (request.ClaimFilter == CaseClaimFilter.MINE)
        { query = query.Where(s => s.State == ModerationSubmissionState.IN_REVIEW && s.ClaimHolderId == request.ActorId); }
        if (request.ClaimFilter == CaseClaimFilter.UNCLAIMED)
        {
            query = query.Where(s => s.State == ModerationSubmissionState.SUBMITTED || !reviewers.Any(u => u.Id == s.ClaimHolderId && u.Id != s.OwnerUserId && u.Id != s.Asset.AuthorId && !exclusions.Any(e => e.SubmissionId == s.Id && e.UserId == u.Id)));
        }
        if (cursor is not null)
        { query = query.Where(s => s.CreatedAt > cursor.CreatedAt || (s.CreatedAt == cursor.CreatedAt && s.Id.CompareTo(cursor.Id) > 0)); }
        var rows = await query.OrderBy(s => s.CreatedAt).ThenBy(s => s.Id).Take(request.PageSize + 1)
            .Select(s => new
            {
                s.Id,
                s.AssetId,
                s.AssetVersionId,
                s.WorkspaceId,
                s.CodeAnalysisReportHeaderId,
                s.SellerEvidenceRevision,
                s.CreatedAt,
                s.Kind,
                s.State,
                s.CaseRevision,
                AssetTitle = s.Asset.Title,
                VersionNumber = s.AssetVersion.VersionNumber,
                Coverage = s.CodeAnalysisReportHeader == null ? (CodeAnalysisCoverageLevel?)null : s.CodeAnalysisReportHeader.CoverageLevel,
                Holder = reviewers.Where(u => u.Id == s.ClaimHolderId && u.Id != s.OwnerUserId && u.Id != s.Asset.AuthorId && !exclusions.Any(e => e.SubmissionId == s.Id && e.UserId == u.Id)).Select(u => new { u.Id, u.Username }).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);
        SellerResponseRequirements.SummaryInput[] inputs = rows.Take(request.PageSize).Where(r => r.CodeAnalysisReportHeaderId.HasValue)
            .Select(r => new SellerResponseRequirements.SummaryInput(r.Id, r.CodeAnalysisReportHeaderId!.Value,
                r.WorkspaceId, r.AssetVersionId, r.SellerEvidenceRevision)).ToArray();
        Dictionary<Guid, SellerResponseRequirements.Summary> summaries = await SellerResponseRequirements.ReadSummaries(dbContext, inputs, cancellationToken);
        var items = new List<ModeratorQueueItem>();
        foreach (var row in rows.Take(request.PageSize))
        {
            summaries.TryGetValue(row.Id, out SellerResponseRequirements.Summary? responses);
            ModerationSubmissionState effectiveState = row.Holder is null ? ModerationSubmissionState.SUBMITTED : row.State;
            items.Add(new(row.Id, row.AssetId, row.AssetTitle, row.VersionNumber, row.CreatedAt, row.Kind, effectiveState,
                row.Holder?.Id, row.Holder?.Username, row.CaseRevision, row.Coverage, responses?.Required.Count ?? 0, responses is not null && (!responses.Available || responses.Outstanding > 0)));
        }
        string? next = null;
        if (rows.Count > request.PageSize)
        {
            var last = rows[request.PageSize - 1];
            DateTimeOffset expiresAt = _timeProvider.GetUtcNow() + QUEUE_CURSOR_LIFETIME;
            next = protector.Protect(JsonSerializer.Serialize(new QueueCursor(request.ActorId, request.State, request.ClaimFilter, request.PageSize, last.CreatedAt, last.Id, expiresAt), CoverageReviewState.JsonOptions), expiresAt);
        }
        return new(ReviewAccessStatus.SUCCESS, new(items, next));
    }
}
