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
    ICheckoutReconciliationHoldStore reconciliationHoldStore,
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
        if (checkoutIntent is null)
        {
            logger.LogError(
                "Paid Stripe checkout references missing intent {CheckoutIntentId}; session {SessionId}",
                verified.CheckoutIntentId,
                verified.StripeSessionId);
            throw new PaymentWebhookMismatchException("Paid Stripe checkout references a missing checkout intent.");
        }

        if (!PaymentMatchesIntent(verified, checkoutIntent))
        {
            logger.LogError(
                "Paid Stripe checkout identity mismatch for intent {CheckoutIntentId}; session {SessionId}",
                verified.CheckoutIntentId,
                verified.StripeSessionId);
            throw new PaymentWebhookMismatchException("Paid Stripe checkout does not match its checkout intent.");
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

        Guid sellerId = items[0].SellerId;
        EmailRecipient? buyer = await userStore.GetEmailRecipientById(verified.UserId, cancellationToken);
        EmailRecipient? seller = null;
        if (sellerId != verified.UserId)
        {
            seller = await userStore.GetEmailRecipientById(sellerId, cancellationToken);
        }

        var orderId = Guid.NewGuid();
        DateTimeOffset purchasedAt = timeProvider.GetUtcNow();
        FulfillmentTransactionResult txResult = new();
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                Guid[] assetIds = items.Select(i => i.AssetId).OrderBy(id => id).ToArray();
                await bundleStore.LockAssetsInOrder(assetIds, ct);

                CheckoutIntent? lockedIntent = await checkoutIntentStore.LockForFulfillment(verified.CheckoutIntentId, ct);
                if (lockedIntent is null)
                {
                    throw new PaymentWebhookMismatchException("Paid Stripe checkout references a missing checkout intent.");
                }

                var lockedItems = lockedIntent.Items.OrderBy(i => i.Position).ToList();
                if (!PaymentMatchesIntent(verified, lockedIntent) || lockedItems.Count == 0)
                {
                    throw new PaymentWebhookMismatchException("Paid Stripe checkout does not match its checkout intent.");
                }

                Order? existingOrder = await orderStore.GetByStripeSessionId(verified.StripeSessionId, ct)
                    ?? await orderStore.GetByCheckoutIntentId(verified.CheckoutIntentId, ct);
                if (existingOrder is not null)
                {
                    txResult.ExistingOrder = existingOrder;
                    return;
                }

                if (await reconciliationHoldStore.HasUnresolvedHoldForCheckoutIntent(verified.CheckoutIntentId, ct))
                {
                    txResult.IdempotentHoldExists = true;
                    return;
                }

                if (!string.IsNullOrWhiteSpace(verified.StripeEventId))
                {
                    var isNewEvent = await processedEventStore.TryRecordEvent(
                        verified.StripeEventId,
                        StripeConstants.Events.CHECKOUT_SESSION_COMPLETED,
                        purchasedAt,
                        ct);
                    if (!isNewEvent)
                    {
                        txResult.DuplicateStripeEvent = true;
                        Order? orderAfterDuplicate = await orderStore.GetByStripeSessionId(verified.StripeSessionId, ct)
                            ?? await orderStore.GetByCheckoutIntentId(verified.CheckoutIntentId, ct);
                        if (orderAfterDuplicate is not null)
                        {
                            txResult.ExistingOrder = orderAfterDuplicate;
                            return;
                        }

                        if (await reconciliationHoldStore.HasUnresolvedHoldForCheckoutIntent(verified.CheckoutIntentId, ct))
                        {
                            txResult.IdempotentHoldExists = true;
                            return;
                        }
                    }
                }

                if (lockedIntent.Status != CheckoutIntentStatus.PENDING)
                {
                    StageHold(verified, lockedIntent, lockedItems, "intent_not_pending", purchasedAt);
                    txResult.FulfillmentHeld = true;
                    await WriteHoldAudit(verified, lockedIntent, "intent_not_pending", ct);
                    return;
                }

                var completed = await checkoutIntentStore.TryCompleteAndRelease(
                    verified.CheckoutIntentId,
                    verified.UserId,
                    verified.StripeSessionId,
                    purchasedAt,
                    ct);
                if (!completed)
                {
                    StageHold(verified, lockedIntent, lockedItems, "intent_completion_race", purchasedAt);
                    txResult.FulfillmentHeld = true;
                    await WriteHoldAudit(verified, lockedIntent, "intent_completion_race", ct);
                    return;
                }

                CheckoutFulfillmentSafety.FulfillmentSafetyResult safety = await CheckoutFulfillmentSafety.ValidateItems(
                    assetStore,
                    lockedItems,
                    ct);
                if (!safety.IsSafe)
                {
                    StageHold(verified, lockedIntent, lockedItems, safety.BlockReason ?? "unsafe_fulfillment", purchasedAt);
                    txResult.FulfillmentHeld = true;
                    await WriteHoldAudit(verified, lockedIntent, safety.BlockReason ?? "unsafe_fulfillment", ct);
                    return;
                }

                (Order order, IReadOnlyList<OrderLine> lines, IReadOnlyList<Purchase> purchases) =
                    orderFactory.CreateOrderWithPurchases(orderId, lockedIntent, lockedItems, verified, purchasedAt);

                txResult.CreatedOrder = await orderStore.CreateWithLinesAndPurchases(order, lines, purchases, ct);

                await notificationPublisher.EnqueueOrderCompletionSideEffects(
                    txResult.CreatedOrder,
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
                    txResult.CreatedOrder.Id.ToString(),
                    new Dictionary<string, object?>
                    {
                        ["checkoutIntentId"] = verified.CheckoutIntentId.ToString(),
                        ["stripeSessionId"] = verified.StripeSessionId,
                        ["itemCount"] = lines.Count,
                        ["assetId"] = txResult.CreatedOrder.AssetId?.ToString(),
                        ["bundleId"] = txResult.CreatedOrder.BundleId?.ToString()
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

        if (txResult.CreatedOrder is not null)
        {
            return ToPayload(txResult.CreatedOrder, sellerId);
        }

        if (txResult.ExistingOrder is not null)
        {
            return ToPayload(txResult.ExistingOrder, sellerId);
        }

        if (txResult.FulfillmentHeld || txResult.IdempotentHoldExists)
        {
            logger.LogWarning(
                "Recorded or reused paid checkout reconciliation hold for intent {CheckoutIntentId}, session {SessionId}",
                verified.CheckoutIntentId,
                verified.StripeSessionId);
            return null;
        }

        if (txResult.DuplicateStripeEvent)
        {
            logger.LogInformation(
                "Stripe webhook event {EventId} was previously processed without a resolvable durable outcome; session {SessionId}",
                verified.StripeEventId,
                verified.StripeSessionId);
            return null;
        }

        Order? existingAfterDuplicate = await orderStore.GetByStripeSessionId(
            verified.StripeSessionId,
            cancellationToken) ?? await orderStore.GetByCheckoutIntentId(
            verified.CheckoutIntentId,
            cancellationToken);

        if (existingAfterDuplicate is not null)
        {
            return ToPayload(existingAfterDuplicate, sellerId);
        }

        throw new InvalidOperationException(
            $"Order unique conflict for session {verified.StripeSessionId} but no durable order was found. Requires reconciliation.");
    }

    private static bool PaymentMatchesIntent(StripeCheckoutCompleted verified, CheckoutIntent checkoutIntent)
    {
        if (checkoutIntent.UserId != verified.UserId
            || checkoutIntent.AmountTotal != verified.AmountTotal
            || !string.Equals(checkoutIntent.Currency, verified.Currency, StringComparison.Ordinal))
        {
            return false;
        }

        if (checkoutIntent.StripeSessionId is not null
            && !string.Equals(checkoutIntent.StripeSessionId, verified.StripeSessionId, StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private async Task WriteHoldAudit(
        StripeCheckoutCompleted verified,
        CheckoutIntent checkoutIntent,
        string reason,
        CancellationToken cancellationToken)
    {
        await auditWriter.Write(new AuditEvent(
            AuditActions.PAYMENT_ORDER_COMPLETED,
            AuditOutcome.DENIED,
            AuditResourceTypes.ORDER,
            checkoutIntent.Id.ToString(),
            new Dictionary<string, object?>
            {
                ["reason"] = reason,
                ["stripeSessionId"] = verified.StripeSessionId,
                ["held"] = true
            },
            ActorTypeOverride: AuditActorType.USER,
            ActorUserIdOverride: verified.UserId), cancellationToken);
    }

    private void StageHold(
        StripeCheckoutCompleted verified,
        CheckoutIntent checkoutIntent,
        IReadOnlyList<CheckoutIntentItem> items,
        string reason,
        DateTimeOffset now)
    {
        reconciliationHoldStore.StageHold(new PaidCheckoutReconciliationHold
        {
            Id = Guid.NewGuid(),
            CheckoutIntentId = checkoutIntent.Id,
            StripeSessionId = verified.StripeSessionId,
            StripeEventId = verified.StripeEventId,
            State = PaidCheckoutReconciliationState.HELD,
            SafePaymentFactsJson = CheckoutFulfillmentSafety.SerializeSafePaymentFacts(verified),
            ItemIdentitiesJson = CheckoutFulfillmentSafety.SerializeItemIdentities(items),
            CreatedAt = now
        });
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

    private sealed class FulfillmentTransactionResult
    {
        public Order? CreatedOrder { get; set; }
        public Order? ExistingOrder { get; set; }
        public bool FulfillmentHeld { get; set; }
        public bool IdempotentHoldExists { get; set; }
        public bool DuplicateStripeEvent { get; set; }
    }
}
