using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsAssetDetail;

internal sealed class GetSellerAnalyticsAssetDetailQueryHandler(
    ISellerAnalyticsStore analyticsStore,
    ITypedCache cache,
    ILogger<GetSellerAnalyticsAssetDetailQueryHandler> logger)
    : IRequestHandler<GetSellerAnalyticsAssetDetailQuery, Result<AnalyticsAssetDetailDto>>
{
    private static readonly TimeSpan _cacheExpiration =
        TimeSpan.FromSeconds(AnalyticsConstants.PRODUCT_DETAIL_CACHE_TTL_SECONDS);

    public async Task<Result<AnalyticsAssetDetailDto>> Handle(
        GetSellerAnalyticsAssetDetailQuery request,
        CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.SellerAnalyticsAssetDetail(
            request.SellerId,
            request.AssetId,
            request.From,
            request.To);

        var cached = await cache.Get<AnalyticsAssetDetailDto>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Seller analytics asset detail cache hit: {Key}", cacheKey);
            return Result.Success(cached);
        }

        var fromUtc = AnalyticsRange.ToUtcStart(request.From);
        var toUtc = AnalyticsRange.ToUtcStart(request.To);
        var granularity = AnalyticsRange.Granularity(request.From, request.To);
        var snapshot = await analyticsStore.GetAssetDetail(
            request.SellerId,
            request.AssetId,
            fromUtc,
            toUtc,
            granularity,
            cancellationToken);

        if (snapshot is null)
        {
            return Result.NotFound();
        }

        var series = AnalyticsRange.BuildSeries(
            snapshot.CommerceDaySeries,
            request.From,
            request.To,
            granularity,
            snapshot.EngagementAvailableFrom,
            snapshot.EngagementDaySeries);

        var dto = new AnalyticsAssetDetailDto(
            request.From,
            request.To,
            "UTC",
            DateTimeOffset.UtcNow,
            AnalyticsConstants.CURRENCY,
            granularity,
            snapshot.EngagementAvailableFrom,
            snapshot.AssetId,
            snapshot.Title,
            snapshot.IsDeleted
                ? AnalyticsProductAvailability.UNAVAILABLE
                : AnalyticsProductAvailability.ACTIVE,
            AnalyticsRange.ToCents(snapshot.GrossRevenue),
            AnalyticsRange.ToCents(snapshot.DirectRevenue),
            AnalyticsRange.ToCents(snapshot.BundleAllocatedRevenue),
            snapshot.Orders,
            snapshot.UnitsSold,
            snapshot.AverageRating,
            snapshot.ReviewCount,
            snapshot.LatestSaleAt,
            snapshot.CheckoutStarts ?? 0,
            snapshot.ProductViews,
            snapshot.UniqueVisitors,
            snapshot.DownloadRequests,
            AnalyticsEngagementMapper.TrackedViewToCheckoutRate(
                snapshot.TrackedCheckoutSessions,
                snapshot.TrackedViewSessions),
            AnalyticsEngagementMapper.CheckoutCompletionRate(
                snapshot.CompletedCheckouts,
                snapshot.CheckoutStarts),
            series);

        await cache.Set(cacheKey, dto, _cacheExpiration, cancellationToken);
        return Result.Success(dto);
    }
}
