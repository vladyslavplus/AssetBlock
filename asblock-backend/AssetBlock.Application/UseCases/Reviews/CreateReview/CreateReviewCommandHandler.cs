using System.Text.Json;
using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Reviews.CreateReview;

internal sealed class CreateReviewCommandHandler(
    IReviewStore reviewStore,
    IPurchaseStore purchaseStore,
    IAssetStore assetStore,
    IUnitOfWork unitOfWork,
    IOutboxStore outboxStore,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<CreateReviewCommandHandler> logger) : IRequestHandler<CreateReviewCommand, Result>
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Result> Handle(CreateReviewCommand request, CancellationToken cancellationToken)
    {
        var asset = await assetStore.GetById(request.AssetId, cancellationToken);
        if (asset is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        var now = DateTimeOffset.UtcNow;
        var purchase = await purchaseStore.GetPurchase(request.UserId, request.AssetId, cancellationToken);
        var creationResult = Review.CreateForPurchase(
            request.AssetId,
            asset.AuthorId,
            request.UserId,
            purchase?.PurchasedAt ?? now,
            request.Rating,
            request.Comment,
            now);

        if (creationResult is { IsSuccess: false, IsOwnAsset: true })
        {
            return Result.Forbidden(ErrorCodes.ERR_CANNOT_REVIEW_OWN_ASSET);
        }

        if (purchase is null)
        {
            logger.LogWarning("CreateReview failed: user {UserId} has not purchased asset {AssetId}", request.UserId, request.AssetId);
            return ResultError.Error(ErrorCodes.ERR_ASSET_NOT_PURCHASED);
        }

        if (!creationResult.IsSuccess && creationResult.IsPurchaseWindowExpired)
        {
            logger.LogWarning("CreateReview failed: user {UserId} purchase expired for review (Asset {AssetId})", request.UserId, request.AssetId);
            return ResultError.Error(ErrorCodes.ERR_REVIEW_TIME_WINDOW_EXPIRED);
        }

        var exists = await reviewStore.Exists(request.UserId, request.AssetId, cancellationToken);
        if (exists)
        {
            logger.LogWarning("CreateReview failed: user {UserId} already reviewed asset {AssetId}", request.UserId, request.AssetId);
            return Result.Conflict(ErrorCodes.ERR_REVIEW_ALREADY_EXISTS);
        }

        var review = creationResult.Review!;

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                await reviewStore.Create(review, ct);

                var metadata = JsonSerializer.Serialize(
                    new ReviewReceivedMessage(asset.Id, asset.Title, request.UserId, request.Rating),
                    _json);

                await outboxStore.Enqueue(
                    OutboxMessageTypes.NOTIFICATION_DISPATCH,
                    new NotificationDispatchPayload(
                        asset.AuthorId,
                        NotificationKind.REVIEW_RECEIVED,
                        NotificationHubMethods.REVIEW_RECEIVED,
                        metadata),
                    ct);

                await auditWriter.Write(new AuditEvent(
                    AuditActions.REVIEW_CREATE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.REVIEW,
                    review.Id.ToString(),
                    new Dictionary<string, object?>
                    {
                        ["assetId"] = request.AssetId.ToString(),
                        ["rating"] = request.Rating
                    }), ct);
            }, cancellationToken);

            try
            {
                await cache.RemoveByPrefix(CacheKeys.ReviewsListAssetPrefix(request.AssetId), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cache invalidation failed after create review for asset {AssetId}", request.AssetId);
            }

            logger.LogInformation("CreateReview succeeded: user {UserId} reviewed asset {AssetId}", request.UserId, request.AssetId);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to create review for user {UserId} and asset {AssetId}", request.UserId, request.AssetId);
            return ResultError.Error(ErrorCodes.ERR_REVIEW_CREATE_FAILED);
        }
    }
}
