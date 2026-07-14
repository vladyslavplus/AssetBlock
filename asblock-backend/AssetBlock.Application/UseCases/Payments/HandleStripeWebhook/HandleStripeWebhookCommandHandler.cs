using System.Text.Json;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
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
    IUnitOfWork unitOfWork,
    IOutboxStore outboxStore,
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

            var purchaseId = Guid.NewGuid();
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
                        PurchasedAt = DateTimeOffset.UtcNow
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
