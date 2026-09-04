using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsCollections;

internal sealed class GetSellerAnalyticsCollectionsQueryHandler(
    ISellerAnalyticsStore analyticsStore,
    ITypedCache cache,
    ILogger<GetSellerAnalyticsCollectionsQueryHandler> logger,
    TimeProvider? timeProvider = null)
    : IRequestHandler<GetSellerAnalyticsCollectionsQuery, Result<AnalyticsCollectionsResult>>
{
    private static readonly TimeSpan _cacheExpiration =
        TimeSpan.FromSeconds(AnalyticsConstants.COLLECTIONS_CACHE_TTL_SECONDS);

    public async Task<Result<AnalyticsCollectionsResult>> Handle(
        GetSellerAnalyticsCollectionsQuery request,
        CancellationToken cancellationToken)
    {
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var cacheKey = CacheKeys.SellerAnalyticsCollections(request.SellerId, request.Request);

        AnalyticsCollectionsResult? cached = await cache.Get<AnalyticsCollectionsResult>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Seller analytics collections cache hit: {Key}", cacheKey);
            return Result.Success(cached);
        }

        DateTimeOffset fromUtc = AnalyticsRange.ToUtcStart(request.Request.From);
        DateTimeOffset toUtc = AnalyticsRange.ToUtcStart(request.Request.To);
        AnalyticsCollectionsRequest req = request.Request;

        (IReadOnlyList<AnalyticsCollectionItem> items, var total, DateTimeOffset? engagementAvailableFrom) =
            await analyticsStore.GetCollectionsPage(
                request.SellerId,
                fromUtc,
                toUtc,
                req.Page,
                req.PageSize,
                req.Sort,
                req.Direction,
                cancellationToken);

        var fullEngagementCoverage =
            engagementAvailableFrom.HasValue && fromUtc >= engagementAvailableFrom.Value;

        if (!fullEngagementCoverage)
        {
            items = items
                .Select(item => item with
                {
                    Views = null,
                    UniqueVisitors = null,
                    ItemClicks = null,
                    ClickThroughRate = null,
                    TopClickedAssets = null
                })
                .ToList();
        }

        var result = new AnalyticsCollectionsResult(
            req.From,
            req.To,
            "UTC",
            AnalyticsConstants.CURRENCY,
            now,
            engagementAvailableFrom,
            items,
            total,
            req.Page,
            req.PageSize);

        await cache.Set(cacheKey, result, _cacheExpiration, cancellationToken);
        return Result.Success(result);
    }
}
