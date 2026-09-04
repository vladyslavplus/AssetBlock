using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal sealed class CheckoutCompletionOrchestrator(
    IAssetStore assetStore,
    IBundleStore bundleStore,
    IOrderStore orderStore,
    ICheckoutIntentStore checkoutIntentStore,
    IUserStore userStore,
    IProcessedStripeWebhookEventStore processedEventStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICheckoutOrderFactory orderFactory,
    ICheckoutNotificationPublisher notificationPublisher,
    TimeProvider timeProvider,
    ILogger<CheckoutCompletionOrchestrator> logger) : ICheckoutCompletionService
{
    public async Task<OrderCompletedPayload?> CompletePaidCheckout(
        StripeCheckoutCompleted verified,
        CancellationToken cancellationToken = default)
    {
        Order? existingBySession = await orderStore.GetByStripeSessionId(
            verified.StripeSessionId,
            cancellationToken);
        if (existingBySession is not null)
        {
            return ToPayload(existingBySession);
        }

        CheckoutIntent? checkoutIntent = await checkoutIntentStore.GetByIdWithItems(
            verified.CheckoutIntentId,
            cancellationToken);
        if (checkoutIntent is null
            || checkoutIntent.Status != CheckoutIntentStatus.PENDING
            || checkoutIntent.UserId != verified.UserId
            || checkoutIntent.AmountTotal != verified.AmountTotal
            || !string.Equals(checkoutIntent.Currency, verified.Currency, StringComparison.Ordinal)
            || (checkoutIntent.StripeSessionId is not null
                && !string.Equals(
                    checkoutIntent.StripeSessionId,
                    verified.StripeSessionId,
                    StringComparison.Ordinal)))
        {
            logger.LogError(
                "Paid Stripe checkout does not match a pending intent. Intent {CheckoutIntentId}, session {SessionId}",
                verified.CheckoutIntentId,
                verified.StripeSessionId);
            throw new PaymentWebhookMismatchException("Paid Stripe checkout does not match its pending checkout intent.");
        }

        var items = checkoutIntent.Items.OrderBy(i => i.Position).ToList();
        if (items.Count == 0)
        {
            logger.LogError(
                "Paid Stripe checkout intent {CheckoutIntentId} has no items; session {SessionId}",
                verified.CheckoutIntentId,
                verified.StripeSessionId);
            throw new PaymentWebhookMismatchException("Paid Stripe checkout references an empty checkout intent.");
        }

        foreach (CheckoutIntentItem item in items)
        {
            AssetVersion? assetVersion = await assetStore.GetVersion(item.AssetId, item.AssetVersionId, cancellationToken);
            if (assetVersion is null)
            {
                logger.LogError(
                    "Paid Stripe checkout references missing AssetVersion {AssetVersionId} on asset {AssetId}; session {SessionId}",
                    item.AssetVersionId,
                    item.AssetId,
                    verified.StripeSessionId);
                throw new PaymentWebhookMismatchException("Paid Stripe checkout references a missing asset version.");
            }
        }

        Guid sellerId = items[0].SellerId;
        EmailRecipient? buyer = await userStore.GetEmailRecipientById(verified.UserId, cancellationToken);
        EmailRecipient? seller = null;
        if (sellerId != verified.UserId)
        {
            seller = await userStore.GetEmailRecipientById(sellerId, cancellationToken);
        }

        var orderId = Guid.NewGuid();
        DateTimeOffset purchasedAt = timeProvider.GetUtcNow();
        var lostCompletionRace = false;
        var isDuplicateEvent = false;
        Order? createdOrder = null;

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                if (!string.IsNullOrWhiteSpace(verified.StripeEventId))
                {
                    var isNewEvent = await processedEventStore.TryRecordEvent(
                        verified.StripeEventId,
                        StripeConstants.Events.CHECKOUT_SESSION_COMPLETED,
                        purchasedAt,
                        ct);
                    if (!isNewEvent)
                    {
                        isDuplicateEvent = true;
                        return;
                    }
                }

                // Claim completion first so concurrent webhooks serialize on the intent row.
                // Then take asset locks before inserting entitlements.
                var completed = await checkoutIntentStore.TryCompleteAndRelease(
                    verified.CheckoutIntentId,
                    verified.UserId,
                    verified.StripeSessionId,
                    purchasedAt,
                    ct);
                if (!completed)
                {
                    lostCompletionRace = true;
                    return;
                }

                Guid[] assetIds = items.Select(i => i.AssetId).OrderBy(id => id).ToArray();
                await bundleStore.LockAssetsInOrder(assetIds, ct);

                (Order order, IReadOnlyList<OrderLine> lines, IReadOnlyList<Purchase> purchases) =
                    orderFactory.CreateOrderWithPurchases(orderId, checkoutIntent, items, verified, purchasedAt);

                createdOrder = await orderStore.CreateWithLinesAndPurchases(order, lines, purchases, ct);

                await notificationPublisher.EnqueueOrderCompletionSideEffects(
                    createdOrder,
                    lines,
                    buyer,
                    seller,
                    sellerId,
                    verified.UserId,
                    purchasedAt,
                    ct);

                await auditWriter.Write(new AuditEvent(
                    AuditActions.PAYMENT_ORDER_COMPLETED,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.ORDER,
                    createdOrder.Id.ToString(),
                    new Dictionary<string, object?>
                    {
                        ["checkoutIntentId"] = verified.CheckoutIntentId.ToString(),
                        ["stripeSessionId"] = verified.StripeSessionId,
                        ["itemCount"] = lines.Count,
                        ["assetId"] = createdOrder.AssetId?.ToString(),
                        ["bundleId"] = createdOrder.BundleId?.ToString()
                    },
                    ActorTypeOverride: AuditActorType.USER,
                    ActorUserIdOverride: verified.UserId), ct);
            }, cancellationToken);
        }
        catch (DuplicateOrderException)
        {
            logger.LogInformation(
                "Idempotent webhook: order unique constraint for session {SessionId}",
                verified.StripeSessionId);
        }
        catch (DuplicateEntitlementException ex)
        {
            logger.LogError(
                ex,
                "Entitlement conflict without durable order for session {SessionId}, intent {CheckoutIntentId}. Requires reconciliation.",
                verified.StripeSessionId,
                verified.CheckoutIntentId);
            throw;
        }

        if (isDuplicateEvent || lostCompletionRace)
        {
            Order? existingAfterRace = await orderStore.GetByStripeSessionId(
                verified.StripeSessionId,
                cancellationToken) ?? await orderStore.GetByCheckoutIntentId(
                verified.CheckoutIntentId,
                cancellationToken);

            if (existingAfterRace is not null)
            {
                logger.LogInformation(
                    "Resolved duplicate event or race for session {SessionId}, order {OrderId}",
                    verified.StripeSessionId,
                    existingAfterRace.Id);
                return ToPayload(existingAfterRace);
            }

            if (isDuplicateEvent)
            {
                logger.LogInformation(
                    "Stripe webhook event {EventId} was previously processed; returning success no-op",
                    verified.StripeEventId);
                return null;
            }

            throw new InvalidOperationException(
                $"Checkout intent {verified.CheckoutIntentId} could not be completed for session {verified.StripeSessionId}.");
        }

        if (createdOrder is not null)
        {
            return ToPayload(createdOrder, sellerId);
        }

        Order? existingAfterDuplicate = await orderStore.GetByStripeSessionId(
            verified.StripeSessionId,
            cancellationToken) ?? await orderStore.GetByCheckoutIntentId(
            verified.CheckoutIntentId,
            cancellationToken);

        if (existingAfterDuplicate is not null)
        {
            return ToPayload(existingAfterDuplicate);
        }

        throw new InvalidOperationException(
            $"Order unique conflict for session {verified.StripeSessionId} but no durable order was found. Requires reconciliation.");
    }

    private static OrderCompletedPayload ToPayload(Order order, Guid? sellerId = null)
    {
        Guid resolvedSellerId = sellerId
            ?? order.Lines.OrderBy(l => l.Position).Select(l => l.SellerId).FirstOrDefault();
        return new OrderCompletedPayload(
            order.Id,
            order.UserId,
            order.AssetId,
            order.BundleId,
            order.ProductTitle,
            order.Lines.Count,
            resolvedSellerId);
    }
}
