using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Reviews.GetReviews;

internal sealed class GetReviewsQueryHandler(
    IReviewStore reviewStore,
    ITypedCache cache,
    ILogger<GetReviewsQueryHandler> logger)
    : IRequestHandler<GetReviewsQuery, Result<Domain.Core.Dto.Paging.PagedResult<ReviewListItem>>>
{
    private static readonly TimeSpan _cacheExpiration = CatalogCacheConstants.REVIEWS_LIST_TTL;

    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<ReviewListItem>>> Handle(GetReviewsQuery request, CancellationToken cancellationToken)
    {
        var key = CacheKeys.ReviewsList(request.AssetId, request.Request);
        var cached = await cache.Get<Domain.Core.Dto.Paging.PagedResult<ReviewListItem>>(key, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Reviews list cache hit for key {Key}", key);
            return Result.Success(cached);
        }

        logger.LogDebug("Reviews list cache miss for key {Key}", key);
        var paged = await reviewStore.GetPaged(request.AssetId, request.Request, cancellationToken);

        var items = paged.Items.Select(r => new ReviewListItem(
            r.Id,
            r.AssetId,
            r.UserId,
            r.User.Username,
            r.Rating,
            r.Comment,
            r.CreatedAt)).ToList();

        var result = new Domain.Core.Dto.Paging.PagedResult<ReviewListItem>(items, paged.TotalCount, paged.Page, paged.PageSize);
        await cache.Set(key, result, _cacheExpiration, cancellationToken);
        return Result.Success(result);
    }
}
