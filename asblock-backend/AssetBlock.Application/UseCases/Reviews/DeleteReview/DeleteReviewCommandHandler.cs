using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Reviews.DeleteReview;

internal sealed class DeleteReviewCommandHandler(
    IReviewStore reviewStore,
    ICacheService cache,
    ILogger<DeleteReviewCommandHandler> logger) : IRequestHandler<DeleteReviewCommand, Result>
{
    public async Task<Result> Handle(DeleteReviewCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var review = await reviewStore.GetById(request.Id, cancellationToken);
            if (review is null)
            {
                return Result.NotFound(ErrorCodes.ERR_REVIEW_NOT_FOUND);
            }

            var deleted = await reviewStore.Delete(request.Id, cancellationToken);
            if (!deleted)
            {
                return Result.NotFound(ErrorCodes.ERR_REVIEW_NOT_FOUND);
            }

            await cache.RemoveByPrefix(CacheKeys.ReviewsListAssetPrefix(review.AssetId), cancellationToken);
            await cache.RemoveByPrefix(CacheKeys.ReviewItem(request.Id), cancellationToken);

            logger.LogInformation("DeleteReview succeeded: deleted review {ReviewId}", request.Id);
            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete review {ReviewId}", request.Id);
            return Result.Error(ErrorCodes.ERR_BAD_REQUEST);
        }
    }
}
