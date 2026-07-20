using System.Text.Json;
using AssetBlock.Application.Common;
using AssetBlock.Application.Services;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal sealed class HandleStripeWebhookCommandHandler(
    IPaymentService paymentService,
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    ICheckoutIntentStore checkoutIntentStore,
    IUserStore userStore,
    IUnitOfWork unitOfWork,
    IOutboxStore outboxStore,
    IAuditWriter auditWriter,
    TransactionalEmailComposer emailComposer,
    ILogger<HandleStripeWebhookCommandHandler> logger)
    : IRequestHandler<HandleStripeWebhookCommand, Result<PurchaseCompletedPayload?>>
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Result<PurchaseCompletedPayload?>> Handle(HandleStripeWebhookCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var verified = await paymentService.VerifyCheckoutCompleted(request.Payload, request.Signature, cancellationToken);
            if (verified is null)
            {
                return Result.Success<PurchaseCompletedPayload?>(null);
            }

            var existingBySession = await purchaseStore.GetByStripePaymentId(verified.StripeSessionId, cancellationToken);
            if (existingBySession is not null)
            {
                return Result.Success<PurchaseCompletedPayload?>(
                    new PurchaseCompletedPayload(existingBySession.UserId, existingBySession.AssetId));
            }

            var checkoutIntent = await checkoutIntentStore.GetById(verified.CheckoutIntentId, cancellationToken);
            if (checkoutIntent is null
                || checkoutIntent.Status != CheckoutIntentStatus.PENDING
                || checkoutIntent.UserId != verified.UserId
                || checkoutIntent.AssetId != verified.AssetId
                || checkoutIntent.AssetVersionId != verified.AssetVersionId
                || checkoutIntent.UnitAmount != verified.AmountTotal
                || !string.Equals(checkoutIntent.Currency, verified.Currency, StringComparison.Ordinal))
            {
                logger.LogError(
                    "Paid Stripe checkout does not match a pending intent. Intent {CheckoutIntentId}, session {SessionId}",
                    verified.CheckoutIntentId,
                    verified.StripeSessionId);
                throw new InvalidOperationException("Paid Stripe checkout does not match its pending checkout intent.");
            }

            var asset = await assetStore.GetById(verified.AssetId, cancellationToken);
            if (asset is null)
            {
                logger.LogError(
                    "Paid Stripe checkout references missing asset {AssetId}; session {SessionId}",
                    verified.AssetId,
                    verified.StripeSessionId);
                throw new InvalidOperationException("Paid Stripe checkout references a missing asset.");
            }

            var buyer = await userStore.GetEmailRecipientById(verified.UserId, cancellationToken);
            EmailRecipient? author = null;
            if (asset.AuthorId != verified.UserId)
            {
                author = await userStore.GetEmailRecipientById(asset.AuthorId, cancellationToken);
            }

            // Verify the version still exists; refuse to create a purchase without a pinned version.
            var assetVersion = await assetStore.GetVersion(verified.AssetId, verified.AssetVersionId, cancellationToken);
            if (assetVersion is null)
            {
                logger.LogError(
                    "Paid Stripe checkout references missing AssetVersion {AssetVersionId} on asset {AssetId}; session {SessionId}",
                    verified.AssetVersionId, verified.AssetId, verified.StripeSessionId);
                throw new InvalidOperationException("Paid Stripe checkout references a missing asset version.");
            }

            var purchaseId = Guid.NewGuid();
            var purchasedAt = DateTimeOffset.UtcNow;
            try
            {
                await unitOfWork.ExecuteInTransaction(async ct =>
                {
                    var completed = await checkoutIntentStore.TryComplete(
                        verified.CheckoutIntentId,
                        verified.UserId,
                        verified.AssetId,
                        verified.AssetVersionId,
                        verified.StripeSessionId,
                        purchasedAt,
                        ct);
                    if (!completed)
                    {
                        throw new InvalidOperationException("Checkout intent could not be completed.");
                    }

                    var purchase = new Purchase
                    {
                        Id = purchaseId,
                        UserId = verified.UserId,
                        AssetId = verified.AssetId,
                        AssetVersionId = verified.AssetVersionId,
                        CheckoutIntentId = verified.CheckoutIntentId,
                        StripePaymentId = verified.StripeSessionId,
                        PricePaid = verified.AmountTotal,
                        Currency = verified.Currency,
                        PurchasedAt = purchasedAt
                    };
                    await purchaseStore.Add(purchase, ct);

                    await outboxStore.Enqueue(
                        OutboxMessageTypes.PURCHASE_COMPLETED,
                        new Domain.Core.Dto.Outbox.PurchaseCompletedPayload(
                            purchaseId,
                            verified.UserId,
                            verified.AssetId,
                            checkoutIntent.AssetTitle,
                            asset.AuthorId),
                        ct);

                    await EnqueueNotification(
                        verified.UserId,
                        NotificationKind.PURCHASE_COMPLETED,
                        NotificationHubMethods.PURCHASE_COMPLETED,
                        new PurchaseCompletedMessage(asset.Id, checkoutIntent.AssetTitle),
                        ct);
                    await EnqueueNotification(
                        verified.UserId,
                        NotificationKind.DOWNLOAD_READY,
                        NotificationHubMethods.DOWNLOAD_READY,
                        new DownloadReadyMessage(asset.Id, checkoutIntent.AssetTitle),
                        ct);
                    if (asset.AuthorId != verified.UserId)
                    {
                        await EnqueueNotification(
                            asset.AuthorId,
                            NotificationKind.ASSET_SOLD,
                            NotificationHubMethods.ASSET_SOLD,
                            new AssetSoldMessage(asset.Id, checkoutIntent.AssetTitle, verified.UserId),
                            ct);
                    }

                    await EnqueuePurchaseEmails(buyer, author, asset, checkoutIntent.AssetTitle, verified.UserId, purchasedAt, ct);

                    await auditWriter.Write(new AuditEvent(
                        AuditActions.PAYMENT_PURCHASE_COMPLETED,
                        AuditOutcome.SUCCESS,
                        AuditResourceTypes.PURCHASE,
                        purchaseId.ToString(),
                        new Dictionary<string, object?>
                        {
                            ["assetId"] = verified.AssetId.ToString(),
                            ["assetVersionId"] = verified.AssetVersionId.ToString(),
                            ["checkoutIntentId"] = verified.CheckoutIntentId.ToString()
                        },
                        ActorTypeOverride: AuditActorType.USER,
                        ActorUserIdOverride: verified.UserId), ct);
                }, cancellationToken);
            }
            catch (DuplicatePurchaseException)
            {
                logger.LogInformation(
                    "Idempotent webhook: purchase already exists for session {SessionId}",
                    verified.StripeSessionId);
            }

            return Result.Success<PurchaseCompletedPayload?>(
                new PurchaseCompletedPayload(verified.UserId, verified.AssetId));
        }
        catch (StripeWebhookInvalidSignatureException)
        {
            return ResultError.Error<PurchaseCompletedPayload?>(ErrorCodes.ERR_STRIPE_WEBHOOK_INVALID);
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

    private async Task EnqueuePurchaseEmails(
        EmailRecipient? buyer,
        EmailRecipient? author,
        Asset asset,
        string assetTitle,
        Guid buyerUserId,
        DateTimeOffset purchasedAt,
        CancellationToken cancellationToken)
    {
        if (buyer is null)
        {
            logger.LogWarning(
                "Skipping purchase receipt email: buyer user {UserId} was not found.",
                buyerUserId);
        }
        else
        {
            var receipt = emailComposer.CreatePurchaseReceipt(
                buyer.Email,
                buyer.Id,
                assetTitle,
                purchasedAt);
            await outboxStore.Enqueue(OutboxMessageTypes.EMAIL_DISPATCH, receipt, cancellationToken);
        }

        if (asset.AuthorId == buyerUserId)
        {
            return;
        }

        if (author is null)
        {
            logger.LogWarning(
                "Skipping asset-sold email: author user {UserId} was not found.",
                asset.AuthorId);
            return;
        }

        var sold = emailComposer.CreateAssetSold(
            author.Email,
            author.Id,
            assetTitle,
            purchasedAt);
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
