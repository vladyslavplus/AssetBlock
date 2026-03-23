using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Reviews.CreateReview;

internal sealed class CreateReviewCommandHandler(
    IReviewStore reviewStore,
    IPurchaseStore purchaseStore,
    IAssetStore assetStore,
    ICacheService cache,
    IRealtimeNotificationPublisher realtimeNotifications,
    ILogger<CreateReviewCommandHandler> logger) : IRequestHandler<CreateReviewCommand, Result>
{
    public async Task<Result> Handle(CreateReviewCommand request, CancellationToken cancellationToken)
    {
        var asset = await assetStore.GetById(request.AssetId, cancellationToken);
        if (asset is null)
        {
            return ResultError.Error(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (asset.AuthorId == request.UserId)
        {
            return ResultError.Error(ErrorCodes.ERR_CANNOT_REVIEW_OWN_ASSET);
        }

        var purchase = await purchaseStore.GetPurchase(request.UserId, request.AssetId, cancellationToken);
        if (purchase is null)
        {
            logger.LogWarning("CreateReview failed: user {UserId} has not purchased asset {AssetId}", request.UserId, request.AssetId);
            return ResultError.Error(ErrorCodes.ERR_ASSET_NOT_PURCHASED);
        }

        var daysSincePurchase = (DateTimeOffset.UtcNow - purchase.PurchasedAt).TotalDays;
        if (daysSincePurchase > BusinessConstants.MAX_REVIEW_DAYS_AFTER_PURCHASE)
        {
            logger.LogWarning("CreateReview failed: user {UserId} purchase expired for review (Asset {AssetId})", request.UserId, request.AssetId);
            return ResultError.Error(ErrorCodes.ERR_REVIEW_TIME_WINDOW_EXPIRED);
        }

        var exists = await reviewStore.Exists(request.UserId, request.AssetId, cancellationToken);
        if (exists)
        {
            logger.LogWarning("CreateReview failed: user {UserId} already reviewed asset {AssetId}", request.UserId, request.AssetId);
            return ResultError.Error(ErrorCodes.ERR_REVIEW_ALREADY_EXISTS);
        }

        try
        {
            await reviewStore.Create(request.AssetId, request.UserId, request.Rating, request.Comment, cancellationToken);
            await cache.RemoveByPrefix(CacheKeys.ReviewsListAssetPrefix(request.AssetId), cancellationToken);
            await realtimeNotifications.NotifyReviewReceived(
                asset.AuthorId,
                asset.Id,
                asset.Title,
                request.UserId,
                request.Rating,
                cancellationToken);
            logger.LogInformation("CreateReview succeeded: user {UserId} reviewed asset {AssetId}", request.UserId, request.AssetId);
            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create review for user {UserId} and asset {AssetId}", request.UserId, request.AssetId);
            return ResultError.Error(ErrorCodes.ERR_REVIEW_CREATE_FAILED);
        }
    }
}
