using System.Text.Json;
using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.Services;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal sealed class HandleStripeWebhookCommandHandler(
    IPaymentService paymentService,
    ICheckoutCompletionService checkoutCompletionService,
    ILogger<HandleStripeWebhookCommandHandler> logger)
    : IRequestHandler<HandleStripeWebhookCommand, Result<OrderCompletedPayload?>>
{
    public async Task<Result<OrderCompletedPayload?>> Handle(
        HandleStripeWebhookCommand request,
        CancellationToken cancellationToken)
    {
        try
        {
            StripeCheckoutCompleted? verified = await paymentService.VerifyCheckoutCompleted(
                request.Payload,
                request.Signature,
                cancellationToken);
            if (verified is null)
            {
                return Result.Success<OrderCompletedPayload?>(null);
            }

            return Result.Success(await checkoutCompletionService.CompletePaidCheckout(verified, cancellationToken));
        }
        catch (StripeWebhookInvalidSignatureException)
        {
            return ResultError.Error<OrderCompletedPayload?>(ErrorCodes.ERR_STRIPE_WEBHOOK_INVALID);
        }
        catch (PaymentWebhookMismatchException ex)
        {
            logger.LogWarning(ex, "Stripe webhook payload mismatch.");
            return ResultError.Error<OrderCompletedPayload?>(ErrorCodes.ERR_PAYMENT_WEBHOOK_MISMATCH);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe webhook processing failed.");
            throw;
        }
    }
}

internal sealed class CheckoutCompletionOrchestrator(
    IAssetStore assetStore,
    IBundleStore bundleStore,
    IOrderStore orderStore,
    ICheckoutIntentStore checkoutIntentStore,
    IUserStore userStore,
    IUnitOfWork unitOfWork,
    IOutboxStore outboxStore,
    IAuditWriter auditWriter,
    TransactionalEmailComposer emailComposer,
    ILogger<CheckoutCompletionOrchestrator> logger) : ICheckoutCompletionService
{
    private const int MAX_EMAIL_ITEM_TITLES = 20;
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

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

        foreach (CheckoutIntentItem? item in items)
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
        DateTimeOffset purchasedAt = DateTimeOffset.UtcNow;
        var lostCompletionRace = false;
        Order? createdOrder = null;

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
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

                var lines = new List<OrderLine>(items.Count);
                var purchases = new List<Purchase>(items.Count);
                foreach (CheckoutIntentItem? item in items)
                {
                    var lineId = Guid.NewGuid();
                    lines.Add(new OrderLine
                    {
                        Id = lineId,
                        OrderId = orderId,
                        AssetId = item.AssetId,
                        AssetVersionId = item.AssetVersionId,
                        SellerId = item.SellerId,
                        Position = item.Position,
                        AssetTitleSnapshot = item.AssetTitleSnapshot,
                        VersionNumber = item.VersionNumber,
                        ListPrice = item.ListPrice,
                        PricePaid = item.AllocatedPrice,
                        LicenseCode = item.LicenseCode,
                        LicenseTemplateVersion = item.LicenseTemplateVersion,
                        LicenseDisplayName = item.LicenseDisplayName,
                        LicenseTerms = item.LicenseTerms
                    });
                    purchases.Add(new Purchase
                    {
                        Id = Guid.NewGuid(),
                        UserId = verified.UserId,
                        AssetId = item.AssetId,
                        AssetVersionId = item.AssetVersionId,
                        OrderLineId = lineId,
                        PurchasedAt = purchasedAt
                    });
                }

                var order = new Order
                {
                    Id = orderId,
                    UserId = verified.UserId,
                    CheckoutIntentId = verified.CheckoutIntentId,
                    AssetId = checkoutIntent.AssetId,
                    BundleId = checkoutIntent.BundleId,
                    BundleRevisionId = checkoutIntent.BundleRevisionId,
                    ProductTitle = checkoutIntent.ProductTitle,
                    StripeSessionId = verified.StripeSessionId,
                    AmountPaid = verified.AmountTotal,
                    Currency = verified.Currency,
                    PurchasedAt = purchasedAt,
                    Lines = lines
                };

                createdOrder = await orderStore.CreateWithLinesAndPurchases(order, lines, purchases, ct);

                await outboxStore.Enqueue(
                    OutboxMessageTypes.ORDER_COMPLETED,
                    ToPayload(createdOrder, sellerId),
                    ct);

                // One buyer notification per order (plan: not per-item, not dual ORDER_COMPLETED+ORDER_READY).
                await EnqueueNotification(
                    verified.UserId,
                    NotificationKind.ORDER_READY,
                    NotificationHubMethods.ORDER_READY,
                    new OrderReadyMessage(
                        createdOrder.Id,
                        createdOrder.ProductTitle,
                        lines.Count,
                        createdOrder.AssetId,
                        createdOrder.BundleId),
                    ct);

                if (sellerId != verified.UserId)
                {
                    await EnqueueNotification(
                        sellerId,
                        NotificationKind.ASSET_SOLD,
                        NotificationHubMethods.ASSET_SOLD,
                        new OrderSoldMessage(
                            createdOrder.Id,
                            createdOrder.ProductTitle,
                            lines.Count,
                            verified.UserId,
                            createdOrder.AssetId,
                            createdOrder.BundleId),
                        ct);
                }

                await EnqueueOrderEmails(
                    buyer,
                    seller,
                    sellerId,
                    createdOrder,
                    lines,
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

        if (lostCompletionRace)
        {
            Order? existingAfterRace = await orderStore.GetByStripeSessionId(
                verified.StripeSessionId,
                cancellationToken);
            if (existingAfterRace is null)
            {
                existingAfterRace = await orderStore.GetByCheckoutIntentId(
                    verified.CheckoutIntentId,
                    cancellationToken);
            }

            if (existingAfterRace is null)
            {
                throw new InvalidOperationException(
                    $"Checkout intent {verified.CheckoutIntentId} could not be completed for session {verified.StripeSessionId}.");
            }

            logger.LogInformation(
                "Idempotent webhook: concurrent delivery lost TryComplete race for session {SessionId}",
                verified.StripeSessionId);
            return ToPayload(existingAfterRace);
        }

        if (createdOrder is not null)
        {
            return ToPayload(createdOrder, sellerId);
        }

        Order? existingAfterDuplicate = await orderStore.GetByStripeSessionId(
            verified.StripeSessionId,
            cancellationToken);
        if (existingAfterDuplicate is null)
        {
            existingAfterDuplicate = await orderStore.GetByCheckoutIntentId(
                verified.CheckoutIntentId,
                cancellationToken);
        }

        if (existingAfterDuplicate is not null)
        {
            return ToPayload(existingAfterDuplicate);
        }

        throw new InvalidOperationException(
            $"Order unique conflict for session {verified.StripeSessionId} but no durable order was found. Requires reconciliation.");
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
