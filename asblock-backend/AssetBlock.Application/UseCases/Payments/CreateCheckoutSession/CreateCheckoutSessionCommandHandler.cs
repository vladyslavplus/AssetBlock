using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;

internal sealed class CreateCheckoutSessionCommandHandler(
    IPaymentService paymentService,
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    ILogger<CreateCheckoutSessionCommandHandler> logger)
    : IRequestHandler<CreateCheckoutSessionCommand, Result<CreateCheckoutSessionResponse>>
{
    public async Task<Result<CreateCheckoutSessionResponse>> Handle(CreateCheckoutSessionCommand request,
        CancellationToken cancellationToken)
    {
        var asset = await assetStore.GetById(request.AssetId, cancellationToken);
        if (asset is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (asset.DeletedAt.HasValue)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (asset.AuthorId == request.UserId)
        {
            return Result.Forbidden(ErrorCodes.ERR_CANNOT_PURCHASE_OWN_ASSET);
        }

        var existingPurchase = await purchaseStore.GetPurchase(request.UserId, request.AssetId, cancellationToken);
        if (existingPurchase is not null)
        {
            return Result.Conflict(ErrorCodes.ERR_ASSET_ALREADY_PURCHASED);
        }

        try
        {
            var sessionUrl = await paymentService.CreateCheckoutSession(
                request.AssetId,
                request.UserId,
                cancellationToken);
            return Result.Success(new CreateCheckoutSessionResponse(sessionUrl));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create checkout session for asset {AssetId}", request.AssetId);
            return ResultError.Error<CreateCheckoutSessionResponse>(ErrorCodes.ERR_PAYMENT_FAILED);
        }
    }
}
