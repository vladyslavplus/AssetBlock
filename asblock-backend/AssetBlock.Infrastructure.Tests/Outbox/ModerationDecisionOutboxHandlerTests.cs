using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence.Stores;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.Outbox;

public sealed class ModerationDecisionOutboxHandlerTests
{
    [Fact]
    public void LifecycleIdentity_WhenSubjectRevisionOrKindChanges_ShouldRemainDeterministicAndDistinct()
    {
        var submissionId = Guid.NewGuid();
        Guid first = ModerationEventIdentity.Event(submissionId, 1, NotificationKind.MODERATION_SUBMITTED, false);
        first.Should().Be(ModerationEventIdentity.Event(submissionId, 1, NotificationKind.MODERATION_SUBMITTED, false));
        first.Should().NotBe(ModerationEventIdentity.Event(submissionId, 2, NotificationKind.MODERATION_SUBMITTED, false));
        first.Should().NotBe(ModerationEventIdentity.Event(submissionId, 1, NotificationKind.MODERATION_WITHDRAWN, false));
        first.Should().NotBe(ModerationEventIdentity.Event(Guid.NewGuid(), 1, NotificationKind.MODERATION_SUBMITTED, false));
        first.Should().NotBe(ModerationEventIdentity.Event(submissionId, 1, NotificationKind.MODERATION_SUBMITTED, true));
        ModerationEventIdentity.Decision(submissionId, false).Should().NotBe(ModerationEventIdentity.Decision(submissionId, true));
    }
    [Fact]
    public async Task Dispatch_WhenDecisionHandlerIsPresent_ShouldProcessWithoutDeadLetter()
    {
        var token = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();
        Guid messageId = ModerationEventIdentity.Decision(decisionId, false);
        var message = new OutboxMessage
        {
            Id = messageId,
            Type = OutboxMessageTypes.MODERATION_DECISION,
            LockToken = token,
            AttemptCount = 1,
            Payload = JsonSerializer.Serialize(new { decisionId, submissionId = Guid.NewGuid(), assetId = Guid.NewGuid(), versionId = Guid.NewGuid(), revision = 1, state = "REJECTED", promotion = "NOT_PROMOTED", isAppeal = false })
        };
        IOutboxStore outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>()).Returns([message]);
        outbox.MarkProcessed(message.Id, token, Arg.Any<CancellationToken>()).Returns(true);
        IRealtimeNotificationPublisher notifications = Substitute.For<IRealtimeNotificationPublisher>();
        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        services.AddSingleton<IOutboxMessageHandler>(new ModerationDecisionOutboxHandler(notifications, Substitute.For<ICacheService>(), Substitute.For<IPublicationEmbeddingEnqueuer>(), Owner(recipientId)));
        await using ServiceProvider provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxDispatcher>.Instance);
        await dispatcher.DispatchBatch(CancellationToken.None);
        await outbox.Received(1).MarkProcessed(message.Id, token, Arg.Any<CancellationToken>());
        await outbox.DidNotReceiveWithAnyArgs().MarkDeadLettered(Guid.Empty, Guid.Empty, null!, CancellationToken.None);
    }
    [Theory]
    [InlineData("CHANGES_REQUESTED", false, NotificationKind.MODERATION_CHANGES_REQUESTED)]
    [InlineData("REJECTED", false, NotificationKind.MODERATION_REJECTED)]
    [InlineData("REJECTED", true, NotificationKind.MODERATION_APPEAL_UPHELD)]
    [InlineData("APPROVED", true, NotificationKind.MODERATION_APPROVED)]
    public async Task Handle_WhenNotPublished_ShouldOnlyDeliverHint(string state, bool isAppeal, NotificationKind kind)
    {
        IRealtimeNotificationPublisher notifications = Substitute.For<IRealtimeNotificationPublisher>();
        ICacheService cache = Substitute.For<ICacheService>();
        IPublicationEmbeddingEnqueuer assets = Substitute.For<IPublicationEmbeddingEnqueuer>();
        var recipientId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();
        Guid messageId = ModerationEventIdentity.Decision(decisionId, isAppeal);
        var message = new OutboxMessage
        {
            Id = messageId,
            Type = OutboxMessageTypes.MODERATION_DECISION,
            Payload = JsonSerializer.Serialize(new { decisionId, submissionId = Guid.NewGuid(), assetId = Guid.NewGuid(), versionId = Guid.NewGuid(), revision = 1, state, promotion = "NOT_PROMOTED", isAppeal })
        };
        var handler = new ModerationDecisionOutboxHandler(notifications, cache, assets, Owner(recipientId));
        await handler.Handle(message, CancellationToken.None);
        await handler.Handle(message, CancellationToken.None);
        await notifications.Received(2).DeliverPersistedNotification(messageId, recipientId, kind, NotificationHubMethods.MODERATION_CASE_CHANGED, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await cache.DidNotReceiveWithAnyArgs().RemoveByPrefix(null!, CancellationToken.None);
        await assets.DidNotReceiveWithAnyArgs().EnqueueApprovedPublicationEmbedding(Guid.Empty, CancellationToken.None);
    }

    [Fact]
    public async Task Handle_WhenPromotionIsInvalid_ShouldFailClosed()
    {
        IRealtimeNotificationPublisher notifications = Substitute.For<IRealtimeNotificationPublisher>();
        var handler = new ModerationDecisionOutboxHandler(notifications, Substitute.For<ICacheService>(), Substitute.For<IPublicationEmbeddingEnqueuer>(), Owner(Guid.NewGuid()));
        var decisionId = Guid.NewGuid();
        Guid messageId = ModerationEventIdentity.Decision(decisionId, false);
        var message = new OutboxMessage
        {
            Id = messageId,
            Type = OutboxMessageTypes.MODERATION_DECISION,
            Payload = JsonSerializer.Serialize(new { decisionId, submissionId = Guid.NewGuid(), assetId = Guid.NewGuid(), versionId = Guid.NewGuid(), revision = 1, state = "REJECTED", promotion = "PROMOTED", isAppeal = false })
        };
        Func<Task> operation = () => handler.Handle(message, CancellationToken.None);
        await operation.Should().ThrowAsync<InvalidOperationException>();
        notifications.ReceivedCalls().Should().BeEmpty();
    }
    [Fact]
    public async Task Handle_WhenApprovedAndPromoted_ShouldDeliverAndRefreshPublication()
    {
        IRealtimeNotificationPublisher notifications = Substitute.For<IRealtimeNotificationPublisher>();
        ICacheService cache = Substitute.For<ICacheService>();
        IPublicationEmbeddingEnqueuer assets = Substitute.For<IPublicationEmbeddingEnqueuer>();
        var assetId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        var decisionId = Guid.NewGuid();
        Guid messageId = ModerationEventIdentity.Decision(decisionId, false);
        var message = new OutboxMessage
        {
            Id = messageId,
            Type = OutboxMessageTypes.MODERATION_DECISION,
            Payload = JsonSerializer.Serialize(new { decisionId, submissionId = Guid.NewGuid(), assetId, versionId = Guid.NewGuid(), revision = 1, state = "APPROVED", promotion = "PROMOTED", isAppeal = false })
        };
        var handler = new ModerationDecisionOutboxHandler(notifications, cache, assets, Owner(recipientId));
        await handler.Handle(message, CancellationToken.None);
        await notifications.Received(1).DeliverPersistedNotification(messageId, recipientId, NotificationKind.MODERATION_APPROVED,
            NotificationHubMethods.MODERATION_CASE_CHANGED, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await cache.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
        await assets.Received(1).EnqueueApprovedPublicationEmbedding(assetId, Arg.Any<CancellationToken>());
    }
    private static IAssetStore Owner(Guid ownerId)
    {
        IAssetStore store = Substitute.For<IAssetStore>();
        store.GetOwnership(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(call => new AssetOwnershipDto(call.Arg<Guid>(), ownerId, false));
        return store;
    }
}
