using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Payments.GetCheckoutStatus;

internal sealed class GetCheckoutStatusQueryHandler(
    ICheckoutIntentStore checkoutIntentStore,
    IOrderStore orderStore)
    : IRequestHandler<GetCheckoutStatusQuery, Result<GetCheckoutStatusResponse>>
{
    public async Task<Result<GetCheckoutStatusResponse>> Handle(
        GetCheckoutStatusQuery request,
        CancellationToken cancellationToken)
    {
        var intent = await checkoutIntentStore.GetByIdWithItems(request.CheckoutIntentId, cancellationToken);
        if (intent is null || intent.UserId != request.UserId)
        {
            return Result.NotFound(ErrorCodes.ERR_NOT_FOUND);
        }

        var order = await orderStore.GetByCheckoutIntentId(intent.Id, cancellationToken);
        if (order is not null || intent.Status == CheckoutIntentStatus.COMPLETED)
        {
            return Result.Success(new GetCheckoutStatusResponse(
                CheckoutFulfillmentStatuses.COMPLETED,
                intent.Id,
                order?.Id,
                intent.ProductTitle,
                intent.AssetId,
                intent.BundleId));
        }

        if (intent.Status == CheckoutIntentStatus.CANCELLED)
        {
            return Result.Success(new GetCheckoutStatusResponse(
                CheckoutFulfillmentStatuses.CANCELLED,
                intent.Id,
                OrderId: null,
                intent.ProductTitle,
                intent.AssetId,
                intent.BundleId));
        }

        return Result.Success(new GetCheckoutStatusResponse(
            CheckoutFulfillmentStatuses.PENDING,
            intent.Id,
            OrderId: null,
            intent.ProductTitle,
            intent.AssetId,
            intent.BundleId));
    }
}
