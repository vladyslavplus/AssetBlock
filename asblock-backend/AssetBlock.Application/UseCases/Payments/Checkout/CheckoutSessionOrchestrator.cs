using AssetBlock.Application.Common;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using Ardalis.Result;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Payments.Checkout;

internal sealed class CheckoutSessionOrchestrator(
    IPaymentService paymentService,
    ICheckoutIntentStore checkoutIntentStore,
    IUnitOfWork unitOfWork,
    ILogger<CheckoutSessionOrchestrator> logger)
{
    private static readonly TimeSpan _checkoutIntentLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan _minimumStripeSessionLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Resumes an open session when possible; otherwise prepares a draft under row locks inside a short
    /// transaction, persists intent/items/reservations, then creates Stripe after commit.
    /// Attribution is captured only when an intent is created, so resuming a pending intent keeps the
    /// original attribution and a later visit cannot re-attribute the sale.
    /// </summary>
    public async Task<Result<CreateCheckoutSessionResponse>> Execute(
        Func<CancellationToken, Task<Result<CheckoutDraft>>> prepareDraftInTransaction,
        Func<CancellationToken, Task<CheckoutIntent?>> getPending,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var pendingIntent = await getPending(cancellationToken);
        if (pendingIntent is not null)
        {
            var resumed = await TryResumeCheckout(pendingIntent, now, cancellationToken);
            if (resumed is not null)
            {
                return resumed;
            }
        }

        CheckoutIntent? createdIntent = null;
        Result<CreateCheckoutSessionResponse>? prepareFailure = null;

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                var draftResult = await prepareDraftInTransaction(ct);
                if (!draftResult.IsSuccess)
                {
                    prepareFailure = MapPrepareFailure(draftResult);
                    return;
                }

                var draft = draftResult.Value;
                var intentId = Guid.NewGuid();
                var expiresAt = DateTimeOffset.UtcNow.Add(_checkoutIntentLifetime);
                var items = draft.Items
                    .OrderBy(i => i.Position)
                    .Select(i => new CheckoutIntentItem
                    {
                        Id = Guid.NewGuid(),
                        CheckoutIntentId = intentId,
                        AssetId = i.AssetId,
                        AssetVersionId = i.AssetVersionId,
                        SellerId = i.SellerId,
                        Position = i.Position,
                        AssetTitleSnapshot = i.AssetTitleSnapshot,
                        VersionNumber = i.VersionNumber,
                        ListPrice = i.ListPrice,
                        AllocatedPrice = i.AllocatedPrice,
                        LicenseCode = i.LicenseCode,
                        LicenseTemplateVersion = i.LicenseTemplateVersion,
                        LicenseDisplayName = i.LicenseDisplayName,
                        LicenseTerms = i.LicenseTerms
                    })
                    .ToList();

                var reservations = items
                    .Select(i => new CheckoutReservation
                    {
                        Id = Guid.NewGuid(),
                        CheckoutIntentId = intentId,
                        UserId = draft.UserId,
                        AssetId = i.AssetId,
                        ExpiresAt = expiresAt
                    })
                    .ToList();

                var intent = new CheckoutIntent
                {
                    Id = intentId,
                    UserId = draft.UserId,
                    AssetId = draft.AssetId,
                    BundleId = draft.BundleId,
                    BundleRevisionId = draft.BundleRevisionId,
                    ProductTitle = draft.ProductTitle,
                    AmountTotal = draft.AmountTotal,
                    Currency = draft.Currency,
                    Status = CheckoutIntentStatus.PENDING,
                    CreatedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = expiresAt,
                    Items = items,
                    Reservations = reservations,
                    AnalyticsVisitorId = draft.Attribution?.VisitorId,
                    AnalyticsSessionId = draft.Attribution?.SessionId,
                    AttributionSource = draft.Attribution?.Source,
                    AttributionCollectionId = draft.Attribution?.CollectionId,
                    AttributionReferrerHost = draft.Attribution?.ReferrerHost
                };

                var assetIds = items.Select(i => i.AssetId).ToArray();
                await checkoutIntentStore.ReleaseExpiredReservations(draft.UserId, assetIds, DateTimeOffset.UtcNow, ct);
                await checkoutIntentStore.CreateWithItemsAndReservations(intent, items, reservations, ct);
                createdIntent = intent;
            }, cancellationToken);
        }
        catch (CheckoutItemReservedException)
        {
            return Result.Conflict(ErrorCodes.ERR_CHECKOUT_ITEM_RESERVED);
        }
        catch (ActiveCheckoutIntentException)
        {
            pendingIntent = await getPending(cancellationToken);
            if (pendingIntent is null)
            {
                return Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
            }

            return await TryResumeCheckout(pendingIntent, DateTimeOffset.UtcNow, cancellationToken)
                ?? Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
        }

        if (prepareFailure is not null)
        {
            return prepareFailure;
        }

        if (createdIntent is null)
        {
            return ResultError.Error<CreateCheckoutSessionResponse>(ErrorCodes.ERR_PAYMENT_FAILED);
        }

        return await CreateStripeSession(createdIntent, cancellationToken);
    }

    private static Result<CreateCheckoutSessionResponse> MapPrepareFailure(Result<CheckoutDraft> draftResult)
    {
        if (draftResult.Status == ResultStatus.NotFound)
        {
            return Result.NotFound(draftResult.Errors.ToArray());
        }

        if (draftResult.Status == ResultStatus.Forbidden)
        {
            return Result.Forbidden(draftResult.Errors.ToArray());
        }

        if (draftResult.Status == ResultStatus.Conflict)
        {
            return Result.Conflict(draftResult.Errors.ToArray());
        }

        return ResultError.Error<CreateCheckoutSessionResponse>(
            draftResult.Errors.FirstOrDefault() ?? ErrorCodes.ERR_PAYMENT_FAILED);
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

                return Result.Success(new CreateCheckoutSessionResponse(session.Url, intent.Id));
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

            return await checkoutIntentStore.TryCancelAndRelease(intent.Id, cancellationToken)
                ? null
                : Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
        }

        if (intent.ExpiresAt - now < _minimumStripeSessionLifetime)
        {
            return await checkoutIntentStore.TryCancelAndRelease(intent.Id, cancellationToken)
                ? null
                : Result.Conflict(ErrorCodes.ERR_CHECKOUT_ALREADY_PENDING);
        }

        return await CreateStripeSession(intent, cancellationToken);
    }

    private async Task<Result<CreateCheckoutSessionResponse>> CreateStripeSession(
        CheckoutIntent intent,
        CancellationToken cancellationToken)
    {
        var lines = (intent.Items)
            .OrderBy(i => i.Position)
            .Select(i => new CheckoutSessionDraftLine(
                i.AssetTitleSnapshot,
                i.AllocatedPrice,
                intent.Currency))
            .ToList();

        if (lines.Count == 0)
        {
            logger.LogError(
                "Checkout intent {CheckoutIntentId} has no items for Stripe session creation",
                intent.Id);
            return ResultError.Error<CreateCheckoutSessionResponse>(ErrorCodes.ERR_PAYMENT_FAILED);
        }

        var draft = new CheckoutSessionDraft(
            intent.Id,
            intent.UserId,
            intent.ExpiresAt,
            intent.Currency,
            lines);

        try
        {
            var session = await paymentService.CreateCheckoutSession(draft, cancellationToken);
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

            return Result.Success(new CreateCheckoutSessionResponse(session.Url, intent.Id));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed to create checkout session for checkout intent {CheckoutIntentId}",
                intent.Id);
            return ResultError.Error<CreateCheckoutSessionResponse>(ErrorCodes.ERR_PAYMENT_FAILED);
        }
    }
}
