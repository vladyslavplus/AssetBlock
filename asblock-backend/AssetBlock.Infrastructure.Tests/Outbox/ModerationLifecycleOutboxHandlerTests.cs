using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Tests.Infrastructure;
using AwesomeAssertions;

namespace AssetBlock.Infrastructure.Tests.Outbox;

public sealed class ModerationLifecycleOutboxHandlerTests
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Handle_WhenMessageIdIsNotDerivedFromEvent_ShouldFailClosed()
    {
        var payload = new ModerationLifecyclePayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null,
            ModerationSubmissionState.SUBMITTED, 1, NotificationKind.MODERATION_SUBMITTED);
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = OutboxMessageTypes.MODERATION_LIFECYCLE,
            Payload = JsonSerializer.Serialize(payload, _json)
        };
        await using ApplicationDbContext db = InMemoryDbContextFactory.Create();
        var handler = new ModerationLifecycleOutboxHandler(db, TimeProvider.System);
        Func<Task> operation = () => handler.Handle(message, CancellationToken.None);
        await operation.Should().ThrowAsync<InvalidOperationException>();
    }

    [Theory]
    [InlineData(NotificationKind.MODERATION_SUBMITTED, ModerationSubmissionState.IN_REVIEW)]
    [InlineData(NotificationKind.MODERATION_REVIEW_STARTED, ModerationSubmissionState.SUBMITTED)]
    [InlineData(NotificationKind.MODERATION_WITHDRAWN, ModerationSubmissionState.APPROVED)]
    [InlineData(NotificationKind.MODERATION_APPROVED, ModerationSubmissionState.APPROVED)]
    public async Task Handle_WhenStateDoesNotMatchLifecycleKind_ShouldFailClosed(NotificationKind kind, ModerationSubmissionState state)
    {
        var payload = new ModerationLifecyclePayload(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, state, 2, kind);
        var message = new OutboxMessage
        {
            Id = ModerationEventIdentity.Event(payload.SubmissionId, payload.Revision, kind),
            Type = OutboxMessageTypes.MODERATION_LIFECYCLE,
            Payload = JsonSerializer.Serialize(payload, _json)
        };
        await using ApplicationDbContext db = InMemoryDbContextFactory.Create();
        var handler = new ModerationLifecycleOutboxHandler(db, TimeProvider.System);
        Func<Task> operation = () => handler.Handle(message, CancellationToken.None);
        await operation.Should().ThrowAsync<InvalidOperationException>();
    }
}
