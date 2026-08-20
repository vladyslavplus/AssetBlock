using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsCollections;

internal sealed class GetSellerAnalyticsCollectionsQueryHandler(
    ISellerAnalyticsStore analyticsStore,
    ITypedCache cache,
    ILogger<GetSellerAnalyticsCollectionsQueryHandler> logger)
    : IRequestHandler<GetSellerAnalyticsCollectionsQuery, Result<AnalyticsCollectionsResult>>
{
    private static readonly TimeSpan _cacheExpiration =
        TimeSpan.FromSeconds(AnalyticsConstants.COLLECTIONS_CACHE_TTL_SECONDS);

    public async Task<Result<AnalyticsCollectionsResult>> Handle(
        GetSellerAnalyticsCollectionsQuery request,
        CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.SellerAnalyticsCollections(request.SellerId, request.Request);

        var cached = await cache.Get<AnalyticsCollectionsResult>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Seller analytics collections cache hit: {Key}", cacheKey);
            return Result.Success(cached);
        }

        var fromUtc = AnalyticsRange.ToUtcStart(request.Request.From);
        var toUtc = AnalyticsRange.ToUtcStart(request.Request.To);
        var req = request.Request;

        (IReadOnlyList<AnalyticsCollectionItem> items, var total, var engagementAvailableFrom) =
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
            DateTimeOffset.UtcNow,
            engagementAvailableFrom,
            items,
            total,
            req.Page,
            req.PageSize);

        await cache.Set(cacheKey, result, _cacheExpiration, cancellationToken);
        return Result.Success(result);
    }
}
