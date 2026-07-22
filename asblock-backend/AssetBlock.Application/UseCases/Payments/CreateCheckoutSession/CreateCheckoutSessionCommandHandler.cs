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
    private static readonly TimeSpan _minimumStripeSessionLifetime = TimeSpan.FromMinutes(30);

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
        var pendingIntent = await checkoutIntentStore.GetPending(
            request.UserId,
            snapshot.AssetId,
            cancellationToken);
        if (pendingIntent is not null)
        {
            var resumed = await TryResumeCheckout(pendingIntent, now, cancellationToken);
            if (resumed is not null)
            {
                return resumed;
            }
        }

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
                await checkoutIntentStore.Create(intent, ct);
            }, cancellationToken);
        }
        catch (ActiveCheckoutIntentException)
        {
            pendingIntent = await checkoutIntentStore.GetPending(
                request.UserId,
                snapshot.AssetId,
                cancellationToken);
            if (pendingIntent is null)
            {
                return Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
            }

            return await TryResumeCheckout(pendingIntent, DateTimeOffset.UtcNow, cancellationToken)
                ?? Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
        }

        return await CreateStripeSession(intent, cancellationToken);
    }

    private async Task<Result<CreateCheckoutSessionResponse>?> TryResumeCheckout(
        CheckoutIntent intent,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(intent.StripeSessionId))
        {
            StripeCheckoutSessionSnapshot session;
            try
            {
                session = await paymentService.GetCheckoutSession(intent.StripeSessionId, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Failed to retrieve Stripe checkout session for checkout intent {CheckoutIntentId}",
                    intent.Id);
                return ResultError.Error<CreateCheckoutSessionResponse>(ErrorCodes.ERR_PAYMENT_FAILED);
            }

            if (string.Equals(
                    session.Status,
                    StripeConstants.CheckoutSessionStatuses.OPEN,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(session.Url))
                {
                    logger.LogError(
                        "Open Stripe checkout session has no URL for checkout intent {CheckoutIntentId}",
                        intent.Id);
                    return ResultError.Error<CreateCheckoutSessionResponse>(ErrorCodes.ERR_PAYMENT_FAILED);
                }

                return Result.Success(new CreateCheckoutSessionResponse(session.Url));
            }

            if (string.Equals(
                    session.Status,
                    StripeConstants.CheckoutSessionStatuses.COMPLETE,
                    StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "Stripe checkout session is complete and awaiting webhook processing for checkout intent {CheckoutIntentId}",
                    intent.Id);
                return Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
            }

            if (!string.Equals(
                    session.Status,
                    StripeConstants.CheckoutSessionStatuses.EXPIRED,
                    StringComparison.OrdinalIgnoreCase))
            {
                logger.LogError(
                    "Stripe checkout session has unsupported status {Status} for checkout intent {CheckoutIntentId}",
                    session.Status,
                    intent.Id);
                return ResultError.Error<CreateCheckoutSessionResponse>(ErrorCodes.ERR_PAYMENT_FAILED);
            }

            return await checkoutIntentStore.TryCancel(intent.Id, cancellationToken)
                ? null
                : Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
        }

        if (intent.ExpiresAt - now < _minimumStripeSessionLifetime)
        {
            return await checkoutIntentStore.TryCancel(intent.Id, cancellationToken)
                ? null
                : Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
        }

        return await CreateStripeSession(intent, cancellationToken);
    }

    private async Task<Result<CreateCheckoutSessionResponse>> CreateStripeSession(
        CheckoutIntent intent,
        CancellationToken cancellationToken)
    {
        var lineItem = new CheckoutLineItem(
            intent.Id,
            intent.AssetId,
            intent.AssetVersionId,
            intent.AssetTitle,
            intent.UnitAmount,
            intent.Currency,
            intent.ExpiresAt);

        try
        {
            var session = await paymentService.CreateCheckoutSession(lineItem, intent.UserId, cancellationToken);
            var sessionStored = await checkoutIntentStore.TrySetStripeSessionId(
                intent.Id,
                session.Id,
                cancellationToken);
            if (!sessionStored)
            {
                logger.LogWarning(
                    "Checkout intent {CheckoutIntentId} changed before Stripe session could be attached",
                    intent.Id);
                return Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
            }

            return Result.Success(new CreateCheckoutSessionResponse(session.Url));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create checkout session for asset {AssetId}", intent.AssetId);
            return ResultError.Error<CreateCheckoutSessionResponse>(ErrorCodes.ERR_PAYMENT_FAILED);
        }
    }
}
