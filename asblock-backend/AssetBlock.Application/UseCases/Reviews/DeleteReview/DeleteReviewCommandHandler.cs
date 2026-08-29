using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Reviews.DeleteReview;

internal sealed class DeleteReviewCommandHandler(
    IReviewStore reviewStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<DeleteReviewCommandHandler> logger) : IRequestHandler<DeleteReviewCommand, Result>
{
    public async Task<Result> Handle(DeleteReviewCommand request, CancellationToken cancellationToken)
    {
        Review? review;
        bool deleted = false;
        try
        {
            review = await reviewStore.GetById(request.Id, cancellationToken);
            if (review is null)
            {
                return Result.NotFound(ErrorCodes.ERR_REVIEW_NOT_FOUND);
            }

            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                deleted = await reviewStore.Delete(request.Id, ct);
                if (deleted)
                {
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.REVIEW_DELETE,
                        AuditOutcome.SUCCESS,
                        AuditResourceTypes.REVIEW,
                        request.Id.ToString()), ct);
                }
            }, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected error deleting review {ReviewId}", request.Id);
            throw;
        }

        if (!deleted)
        {
            return Result.NotFound(ErrorCodes.ERR_REVIEW_NOT_FOUND);
        }

        try
        {
            await cache.RemoveByPrefix(CacheKeys.ReviewsListAssetPrefix(review.AssetId), cancellationToken);
            await cache.RemoveByPrefix(CacheKeys.ReviewItem(request.Id), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Cache invalidation failed after deleting review {ReviewId}", request.Id);
        }

        logger.LogInformation("DeleteReview succeeded: deleted review {ReviewId}", request.Id);
        return Result.Success();
    }
}
