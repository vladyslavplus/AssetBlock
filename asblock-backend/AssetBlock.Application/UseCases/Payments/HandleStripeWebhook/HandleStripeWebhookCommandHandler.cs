using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Payments;
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
