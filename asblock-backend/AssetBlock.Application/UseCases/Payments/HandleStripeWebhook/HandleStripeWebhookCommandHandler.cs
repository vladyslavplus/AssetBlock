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

            var asset = await assetStore.GetById(verified.AssetId, cancellationToken);
            if (asset is null)
            {
                logger.LogWarning("Checkout completed for missing asset {AssetId}; ignoring.", verified.AssetId);
                return Result.Success<PurchaseCompletedPayload?>(null);
            }

            var buyer = await userStore.GetEmailRecipientById(verified.UserId, cancellationToken);
            EmailRecipient? author = null;
            if (asset.AuthorId != verified.UserId)
            {
                author = await userStore.GetEmailRecipientById(asset.AuthorId, cancellationToken);
            }

            var purchaseId = Guid.NewGuid();
            var purchasedAt = DateTimeOffset.UtcNow;
            try
            {
                await unitOfWork.ExecuteInTransaction(async ct =>
                {
                    var purchase = new Purchase
                    {
                        Id = purchaseId,
                        UserId = verified.UserId,
                        AssetId = verified.AssetId,
                        StripePaymentId = verified.StripeSessionId,
                        PurchasedAt = purchasedAt
                    };
                    await purchaseStore.Add(purchase, ct);

                    await outboxStore.Enqueue(
                        OutboxMessageTypes.PURCHASE_COMPLETED,
                        new Domain.Core.Dto.Outbox.PurchaseCompletedPayload(
                            purchaseId,
                            verified.UserId,
                            verified.AssetId,
                            asset.Title,
                            asset.AuthorId),
                        ct);

                    await EnqueueNotification(
                        verified.UserId,
                        NotificationKind.PURCHASE_COMPLETED,
                        NotificationHubMethods.PURCHASE_COMPLETED,
                        new PurchaseCompletedMessage(asset.Id, asset.Title),
                        ct);
                    await EnqueueNotification(
                        verified.UserId,
                        NotificationKind.DOWNLOAD_READY,
                        NotificationHubMethods.DOWNLOAD_READY,
                        new DownloadReadyMessage(asset.Id, asset.Title),
                        ct);
                    if (asset.AuthorId != verified.UserId)
                    {
                        await EnqueueNotification(
                            asset.AuthorId,
                            NotificationKind.ASSET_SOLD,
                            NotificationHubMethods.ASSET_SOLD,
                            new AssetSoldMessage(asset.Id, asset.Title, verified.UserId),
                            ct);
                    }

                    await EnqueuePurchaseEmails(buyer, author, asset, verified.UserId, purchasedAt, ct);

                    await auditWriter.Write(new AuditEvent(
                        AuditActions.PAYMENT_PURCHASE_COMPLETED,
                        AuditOutcome.SUCCESS,
                        AuditResourceTypes.PURCHASE,
                        purchaseId.ToString(),
                        new Dictionary<string, object?> { ["assetId"] = verified.AssetId.ToString() },
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
                asset.Title,
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
            asset.Title,
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
