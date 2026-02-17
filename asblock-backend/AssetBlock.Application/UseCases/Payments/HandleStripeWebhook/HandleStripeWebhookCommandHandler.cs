using AssetBlock.Domain.Abstractions.Services;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal sealed class HandleStripeWebhookCommandHandler(IPaymentService paymentService)
    : IRequestHandler<HandleStripeWebhookCommand, Result<PurchaseCompletedPayload?>>
{
    public async Task<Result<PurchaseCompletedPayload?>> Handle(HandleStripeWebhookCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await paymentService.HandleCheckoutCompleted(request.Payload, request.Signature, cancellationToken);
            if (result is null)
            {
                return Result.Success<PurchaseCompletedPayload?>(null);
            }

            (Guid userId, Guid assetId) = result.Value;
            return Result.Success<PurchaseCompletedPayload?>(new PurchaseCompletedPayload(userId, assetId));
        }
        catch (Exception ex)
        {
            return Result.Error($"Webhook processing failed: {ex.Message}");
        }
    }
}
