using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Hubs;
using AssetBlock.WebApi.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.Services;

public sealed class RealtimeNotificationPublisherTests
{
    [Theory]
    [InlineData(NotificationKind.ORDER_READY)]
    [InlineData(NotificationKind.MODERATION_APPEAL_UPHELD)]
    [InlineData(NotificationKind.MODERATION_APPROVED)]
    public async Task DeliverPersistedNotification_WhenDeliveredTwice_ShouldPersistOnlyOnce(NotificationKind kind)
    {
        var outboxId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        UserNotification? persisted = null;
        INotificationStore store = Substitute.For<INotificationStore>();
        store.GetBySourceOutboxMessageId(outboxId, Arg.Any<CancellationToken>())
            .Returns(_ => persisted);
        store.Add(Arg.Any<UserNotification>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                persisted = callInfo.Arg<UserNotification>();
                return persisted;
            });

        IClientProxy clientProxy = Substitute.For<IClientProxy>();
        clientProxy.SendCoreAsync(
                Arg.Any<string>(),
                Arg.Any<object?[]>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        IHubClients clients = Substitute.For<IHubClients>();
        clients.User(recipientId.ToString()).Returns(clientProxy);
        IHubContext<NotificationsHub> hubContext = Substitute.For<IHubContext<NotificationsHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new RealtimeNotificationPublisher(
            hubContext,
            store,
            NullLogger<RealtimeNotificationPublisher>.Instance);

        await publisher.DeliverPersistedNotification(
            outboxId,
            recipientId,
            kind,
            "OrderReady",
            "{}",
            CancellationToken.None);
        await publisher.DeliverPersistedNotification(
            outboxId,
            recipientId,
            kind,
            "OrderReady",
            "{}",
            CancellationToken.None);

        await store.Received(1).Add(
            Arg.Is<UserNotification>(notification =>
                notification.SourceOutboxMessageId == outboxId &&
                notification.RecipientUserId == recipientId && notification.Kind == kind),
            Arg.Any<CancellationToken>());
    }
}
