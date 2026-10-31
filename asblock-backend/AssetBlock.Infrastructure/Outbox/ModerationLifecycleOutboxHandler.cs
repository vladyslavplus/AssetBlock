using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed class ModerationLifecycleOutboxHandler(ApplicationDbContext db, TimeProvider clock) : IOutboxMessageHandler
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    public string MessageType => OutboxMessageTypes.MODERATION_LIFECYCLE;

    public async Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        ModerationLifecyclePayload payload = JsonSerializer.Deserialize<ModerationLifecyclePayload>(message.Payload, _json)
            ?? throw new InvalidOperationException("Invalid moderation lifecycle event.");
        if (payload.SubmissionId == Guid.Empty || payload.AssetId == Guid.Empty || payload.VersionId == Guid.Empty || payload.OwnerId == Guid.Empty
            || payload.Revision <= 0 || payload.Kind is not (NotificationKind.MODERATION_SUBMITTED or NotificationKind.MODERATION_REVIEW_STARTED or NotificationKind.MODERATION_WITHDRAWN)
            || (payload.Kind == NotificationKind.MODERATION_SUBMITTED && payload.State != ModerationSubmissionState.SUBMITTED)
            || (payload.Kind == NotificationKind.MODERATION_REVIEW_STARTED && payload.State != ModerationSubmissionState.IN_REVIEW)
            || (payload.Kind == NotificationKind.MODERATION_WITHDRAWN && payload.State != ModerationSubmissionState.WITHDRAWN)
            || message.Id != ModerationEventIdentity.Event(payload.SubmissionId, payload.Revision, payload.Kind))
        { throw new InvalidOperationException("Invalid moderation lifecycle event."); }
        var metadata = JsonSerializer.Serialize(new { payload.SubmissionId, payload.AssetId, payload.VersionId, state = payload.State.ToString(), payload.Revision }, _json);
        DateTimeOffset now = clock.GetUtcNow();
        Guid ownerId = await db.Assets.AsNoTracking().Where(a => a.Id == payload.AssetId).Select(a => a.AuthorId).SingleOrDefaultAsync(cancellationToken);
        if (ownerId == Guid.Empty)
        { throw new InvalidOperationException("Moderation event asset is unavailable."); }
        await AddRecipients(message.Id, [ownerId], payload.Kind, metadata, now, cancellationToken);
        if (payload.Kind == NotificationKind.MODERATION_WITHDRAWN && payload.ReviewerId is Guid reviewer && reviewer != payload.OwnerId && reviewer != ownerId
            && await db.Users.Where(AppRoles.CanReviewUserExpression).AnyAsync(u => u.Id == reviewer, cancellationToken))
        { await AddRecipients(message.Id, [reviewer], payload.Kind, metadata, now, cancellationToken); }
        if (payload.Kind == NotificationKind.MODERATION_SUBMITTED)
        {
            Guid? cursor = null;
            while (true)
            {
                IQueryable<User> reviewers = db.Users.AsNoTracking().Where(AppRoles.CanReviewUserExpression).Where(u => u.Id != payload.OwnerId && u.Id != ownerId);
                if (cursor is Guid last)
                { reviewers = reviewers.Where(u => u.Id.CompareTo(last) > 0); }
                List<Guid> batch = await reviewers.OrderBy(u => u.Id).Select(u => u.Id).Take(100).ToListAsync(cancellationToken);
                await AddRecipients(message.Id, batch, payload.Kind, metadata, now, cancellationToken);
                if (batch.Count < 100)
                { break; }
                cursor = batch[^1];
            }
        }
    }

    private async Task AddRecipients(Guid eventId, IReadOnlyList<Guid> recipients, NotificationKind kind, string metadata, DateTimeOffset now, CancellationToken ct)
    {
        if (recipients.Count == 0)
        { return; }
        Guid[] recipientIds = recipients.ToArray();
        Guid[] childIds = recipientIds.Select(id => ModerationEventIdentity.Recipient(eventId, id)).ToArray();
        var payloads = recipientIds.Select(id => JsonSerializer.Serialize(new NotificationDispatchPayload(id, kind, NotificationHubMethods.MODERATION_CASE_CHANGED, metadata), _json)).ToArray();
        var kindName = kind.ToString();
        await using IDbContextTransaction tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO outbox_messages ("Id", "Type", "Payload", "OccurredAt", "Status", "AttemptCount", "ReplayCount")
            SELECT child_id, {OutboxMessageTypes.NOTIFICATION_DISPATCH}, payload, {now}, {(int)OutboxMessageStatus.PENDING}, 0, 0
            FROM unnest({childIds}, {payloads}) AS entries(child_id, payload)
            ON CONFLICT ("Id") DO NOTHING
            """, ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_notifications ("Id", "RecipientUserId", "Kind", "MetadataJson", "CreatedAt", "SourceOutboxMessageId")
            SELECT child_id, recipient_id, {kindName}, {metadata}, {now}, child_id
            FROM unnest({childIds}, {recipientIds}) AS entries(child_id, recipient_id)
            ON CONFLICT ("SourceOutboxMessageId") WHERE "SourceOutboxMessageId" IS NOT NULL DO NOTHING
            """, ct);
        await tx.CommitAsync(ct);
    }
}
