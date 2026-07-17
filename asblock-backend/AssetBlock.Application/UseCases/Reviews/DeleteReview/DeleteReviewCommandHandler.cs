using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using MediatR;
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
        try
        {
            var review = await reviewStore.GetById(request.Id, cancellationToken);
            if (review is null)
            {
                return Result.NotFound(ErrorCodes.ERR_REVIEW_NOT_FOUND);
            }

            bool deleted = false;
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

            if (!deleted)
            {
                return Result.NotFound(ErrorCodes.ERR_REVIEW_NOT_FOUND);
            }

            await cache.RemoveByPrefix(CacheKeys.ReviewsListAssetPrefix(review.AssetId), cancellationToken);
            await cache.RemoveByPrefix(CacheKeys.ReviewItem(request.Id), cancellationToken);

            logger.LogInformation("DeleteReview succeeded: deleted review {ReviewId}", request.Id);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete review {ReviewId}", request.Id);
            return Result.Error(ErrorCodes.ERR_INTERNAL);
        }
    }
}
