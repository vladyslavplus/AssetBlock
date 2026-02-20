using System.Text.Json;
using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Reviews;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Reviews.GetReviewById;

internal sealed class GetReviewByIdQueryHandler(
    IReviewStore reviewStore,
    ICacheService cache,
    ILogger<GetReviewByIdQueryHandler> logger)
    : IRequestHandler<GetReviewByIdQuery, Result<ReviewDetailItem>>
{
    private static readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Result<ReviewDetailItem>> Handle(GetReviewByIdQuery request, CancellationToken cancellationToken)
    {
        var key = CacheKeys.ReviewItem(request.Id);
        var cached = await cache.GetString(key, cancellationToken);
        if (cached is not null)
        {
            try
            {
                var cachedResult = JsonSerializer.Deserialize<ReviewDetailItem>(cached, _jsonOptions);
                if (cachedResult is not null)
                {
                    logger.LogDebug("Review profile cache hit for key {Key}", key);
                    return Result.Success(cachedResult);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid review profile cache payload for key {Key}", key);
                await cache.RemoveByPrefix(key, cancellationToken);
            }
        }
        else
        {
            logger.LogDebug("Review item cache miss for key {Key}", key);
        }

        var review = await reviewStore.GetById(request.Id, cancellationToken);
        if (review is null)
        {
            return ResultError.Error<ReviewDetailItem>(ErrorCodes.ERR_REVIEW_NOT_FOUND);
        }

        var item = new ReviewDetailItem(
            review.Id,
            review.AssetId,
            review.UserId,
            review.User.Username,
            review.Rating,
            review.Comment,
            review.CreatedAt);

        await cache.SetString(key, JsonSerializer.Serialize(item, _jsonOptions), _cacheExpiration, cancellationToken);
        return Result.Success(item);
    }
}
