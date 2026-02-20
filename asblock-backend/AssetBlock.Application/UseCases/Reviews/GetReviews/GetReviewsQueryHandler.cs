using System.Text.Json;
using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Reviews;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Reviews.GetReviews;

internal sealed class GetReviewsQueryHandler(
    IReviewStore reviewStore,
    ICacheService cache,
    ILogger<GetReviewsQueryHandler> logger)
    : IRequestHandler<GetReviewsQuery, Result<Domain.Core.Dto.Paging.PagedResult<ReviewListItem>>>
{
    private static readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<ReviewListItem>>> Handle(GetReviewsQuery request, CancellationToken cancellationToken)
    {
        var key = CacheKeys.ReviewsList(request.AssetId, request.Request);
        var cached = await cache.GetString(key, cancellationToken);
        if (cached is not null)
        {
            try
            {
                var cachedResult = JsonSerializer.Deserialize<Domain.Core.Dto.Paging.PagedResult<ReviewListItem>>(cached, _jsonOptions);
                if (cachedResult is not null)
                {
                    logger.LogDebug("Reviews list cache hit for key {Key}", key);
                    return Result.Success(cachedResult);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid reviews list cache payload for key {Key}", key);
                await cache.RemoveByPrefix(CacheKeys.ReviewsListAssetPrefix(request.AssetId), cancellationToken);
            }
        }
        else
        {
            logger.LogDebug("Reviews list cache miss for key {Key}", key);
        }

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
        await cache.SetString(key, JsonSerializer.Serialize(result, _jsonOptions), _cacheExpiration, cancellationToken);
        return Result.Success(result);
    }
}
