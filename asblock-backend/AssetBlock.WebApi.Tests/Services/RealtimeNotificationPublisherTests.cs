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
    [Fact]
    public async Task DeliverPersistedNotification_WhenDeliveredTwice_ShouldPersistOnlyOnce()
    {
        var outboxId = Guid.NewGuid();
        var recipientId = Guid.NewGuid();
        UserNotification? persisted = null;
        var store = Substitute.For<INotificationStore>();
        store.GetBySourceOutboxMessageId(outboxId, Arg.Any<CancellationToken>())
            .Returns(_ => persisted);
        store.Add(Arg.Any<UserNotification>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                persisted = callInfo.Arg<UserNotification>();
                return persisted;
            });

        var clientProxy = Substitute.For<IClientProxy>();
        clientProxy.SendCoreAsync(
                Arg.Any<string>(),
                Arg.Any<object?[]>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        var clients = Substitute.For<IHubClients>();
        clients.User(recipientId.ToString()).Returns(clientProxy);
        var hubContext = Substitute.For<IHubContext<NotificationsHub>>();
        hubContext.Clients.Returns(clients);
        var publisher = new RealtimeNotificationPublisher(
            hubContext,
            store,
            NullLogger<RealtimeNotificationPublisher>.Instance);

        await publisher.DeliverPersistedNotification(
            outboxId,
            recipientId,
            NotificationKind.PURCHASE_COMPLETED,
            "PurchaseCompleted",
            "{}",
            CancellationToken.None);
        await publisher.DeliverPersistedNotification(
            outboxId,
            recipientId,
            NotificationKind.PURCHASE_COMPLETED,
            "PurchaseCompleted",
            "{}",
            CancellationToken.None);

        await store.Received(1).Add(
            Arg.Is<UserNotification>(notification =>
                notification.SourceOutboxMessageId == outboxId &&
                notification.RecipientUserId == recipientId),
            Arg.Any<CancellationToken>());
    }
}
