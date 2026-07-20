using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;

internal sealed class CreateCheckoutSessionCommandHandler(
    IPaymentService paymentService,
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    ICheckoutIntentStore checkoutIntentStore,
    IUnitOfWork unitOfWork,
    ILogger<CreateCheckoutSessionCommandHandler> logger)
    : IRequestHandler<CreateCheckoutSessionCommand, Result<CreateCheckoutSessionResponse>>
{
    private static readonly TimeSpan _checkoutIntentLifetime = TimeSpan.FromHours(24);

    public async Task<Result<CreateCheckoutSessionResponse>> Handle(CreateCheckoutSessionCommand request,
        CancellationToken cancellationToken)
    {
        var snapshot = await assetStore.GetCurrentVersionSnapshot(request.AssetId, cancellationToken);
        if (snapshot is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (snapshot.DeletedAt.HasValue)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (snapshot.AuthorId == request.UserId)
        {
            return Result.Forbidden(ErrorCodes.ERR_CANNOT_PURCHASE_OWN_ASSET);
        }

        var existingPurchase = await purchaseStore.GetPurchase(request.UserId, request.AssetId, cancellationToken);
        if (existingPurchase is not null)
        {
            return Result.Conflict(ErrorCodes.ERR_ASSET_ALREADY_PURCHASED);
        }

        var now = DateTimeOffset.UtcNow;
        var intent = new CheckoutIntent
        {
            Id = Guid.NewGuid(),
            UserId = request.UserId,
            AssetId = snapshot.AssetId,
            AssetVersionId = snapshot.AssetVersionId,
            AssetTitle = snapshot.Title,
            UnitAmount = snapshot.Price,
            Currency = StripeConstants.CURRENCY_USD,
            Status = CheckoutIntentStatus.PENDING,
            CreatedAt = now,
            ExpiresAt = now.Add(_checkoutIntentLifetime)
        };

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                await checkoutIntentStore.CancelExpiredPending(request.UserId, snapshot.AssetId, now, ct);
                await checkoutIntentStore.Create(intent, ct);
            }, cancellationToken);
        }
        catch (ActiveCheckoutIntentException)
        {
            return Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
        }

        var lineItem = new CheckoutLineItem(
            intent.Id,
            snapshot.AssetId,
            snapshot.AssetVersionId,
            snapshot.Title,
            snapshot.Price,
            StripeConstants.CURRENCY_USD);

        try
        {
            var session = await paymentService.CreateCheckoutSession(lineItem, request.UserId, cancellationToken);
            await checkoutIntentStore.TrySetStripeSessionId(intent.Id, session.Id, cancellationToken);
            return Result.Success(new CreateCheckoutSessionResponse(session.Url));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create checkout session for asset {AssetId}", request.AssetId);
            return ResultError.Error<CreateCheckoutSessionResponse>(ErrorCodes.ERR_PAYMENT_FAILED);
        }
    }
}
