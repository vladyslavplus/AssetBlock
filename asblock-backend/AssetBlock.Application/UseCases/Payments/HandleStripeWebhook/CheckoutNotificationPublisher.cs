using System.Text.Json;
using AssetBlock.Application.Services;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal sealed class CheckoutNotificationPublisher(
    IOutboxStore outboxStore,
    TransactionalEmailComposer emailComposer,
    ILogger<CheckoutNotificationPublisher> logger) : ICheckoutNotificationPublisher
{
    private const int MAX_EMAIL_ITEM_TITLES = 20;
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task EnqueueOrderCompletionSideEffects(
        Order order,
        IReadOnlyList<OrderLine> lines,
        EmailRecipient? buyer,
        EmailRecipient? sellerRecipient,
        Guid sellerId,
        Guid buyerUserId,
        DateTimeOffset purchasedAt,
        CancellationToken cancellationToken = default)
    {
        // One buyer notification per order
        await EnqueueNotification(
            buyerUserId,
            NotificationKind.ORDER_READY,
            NotificationHubMethods.ORDER_READY,
            new OrderReadyMessage(
                order.Id,
                order.ProductTitle,
                lines.Count,
                order.AssetId,
                order.BundleId),
            cancellationToken);

        if (sellerId != buyerUserId)
        {
            await EnqueueNotification(
                sellerId,
                NotificationKind.ASSET_SOLD,
                NotificationHubMethods.ASSET_SOLD,
                new OrderSoldMessage(
                    order.Id,
                    order.ProductTitle,
                    lines.Count,
                    buyerUserId,
                    order.AssetId,
                    order.BundleId),
                cancellationToken);
        }

        await EnqueueOrderEmails(
            buyer,
            sellerRecipient,
            sellerId,
            order,
            lines,
            buyerUserId,
            purchasedAt,
            cancellationToken);
    }

    private async Task EnqueueOrderEmails(
        EmailRecipient? buyer,
        EmailRecipient? sellerRecipient,
        Guid sellerId,
        Order order,
        IReadOnlyList<OrderLine> lines,
        Guid buyerUserId,
        DateTimeOffset purchasedAt,
        CancellationToken cancellationToken)
    {
        var itemTitles = lines
            .OrderBy(l => l.Position)
            .Select(l => l.AssetTitleSnapshot)
            .Take(MAX_EMAIL_ITEM_TITLES)
            .ToArray();

        if (buyer is null)
        {
            logger.LogWarning(
                "Skipping order receipt email: buyer user {UserId} was not found.",
                buyerUserId);
        }
        else
        {
            EmailDispatchPayload receipt = emailComposer.CreateOrderReceipt(
                buyer.Email,
                buyer.Id,
                order.ProductTitle,
                order.AmountPaid,
                order.Currency,
                purchasedAt,
                itemTitles);
            await outboxStore.Enqueue(OutboxMessageTypes.EMAIL_DISPATCH, receipt, cancellationToken);
        }

        if (sellerId == buyerUserId)
        {
            return;
        }

        if (sellerRecipient is null)
        {
            logger.LogWarning(
                "Skipping order-sold email: seller user {UserId} was not found.",
                sellerId);
            return;
        }

        EmailDispatchPayload sold = emailComposer.CreateOrderSold(
            sellerRecipient.Email,
            sellerRecipient.Id,
            order.ProductTitle,
            order.AmountPaid,
            order.Currency,
            purchasedAt,
            itemTitles);
        await outboxStore.Enqueue(OutboxMessageTypes.EMAIL_DISPATCH, sold, cancellationToken);
    }

    private Task EnqueueNotification<T>(
        Guid recipientUserId,
        NotificationKind kind,
        string hubMethod,
        T payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, _json);
        return outboxStore.Enqueue(
            OutboxMessageTypes.NOTIFICATION_DISPATCH,
            new NotificationDispatchPayload(recipientUserId, kind, hubMethod, json),
            cancellationToken);
    }
}
