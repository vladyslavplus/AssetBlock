using AssetBlock.Domain.Abstractions.Services;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal sealed class HandleStripeWebhookCommandHandler(
    IPaymentService paymentService,
    IAssetStore assetStore,
    IRealtimeNotificationPublisher realtimeNotifications,
    ILogger<HandleStripeWebhookCommandHandler> logger)
    : IRequestHandler<HandleStripeWebhookCommand, Result<PurchaseCompletedPayload?>>
{
    public async Task<Result<PurchaseCompletedPayload?>> Handle(HandleStripeWebhookCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var result =
                await paymentService.HandleCheckoutCompleted(request.Payload, request.Signature, cancellationToken);
            if (result is null)
            {
                return Result.Success<PurchaseCompletedPayload?>(null);
            }

            (Guid userId, Guid assetId) = result.Value;
            var asset = await assetStore.GetById(assetId, cancellationToken);
            if (asset is not null)
            {
                await realtimeNotifications.NotifyPurchaseCompleted(userId, asset.Id, asset.Title, cancellationToken);
                await realtimeNotifications.NotifyDownloadReady(userId, asset.Id, asset.Title, cancellationToken);
                if (asset.AuthorId != userId)
                {
                    await realtimeNotifications.NotifyAssetSold(asset.AuthorId, asset.Id, asset.Title, userId, cancellationToken);
                }
            }
            else
            {
                logger.LogWarning("Checkout completed for asset {AssetId} but asset not found; skipping real-time notifications.", assetId);
            }

            return Result.Success<PurchaseCompletedPayload?>(new PurchaseCompletedPayload(userId, assetId));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe webhook processing failed.");
            return Result.Error("Stripe webhook processing failed.");
        }
    }
}
