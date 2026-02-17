using AssetBlock.Domain.Abstractions.Services;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal sealed class HandleStripeWebhookCommandHandler(
    IPaymentService paymentService,
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
            return Result.Success<PurchaseCompletedPayload?>(new PurchaseCompletedPayload(userId, assetId));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stripe webhook processing failed.");
            return Result.Error("Webhook processing failed.");
        }
    }
}
