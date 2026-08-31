using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Payments;
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

        AnalyticsAssetDetailDto? cached = await cache.Get<AnalyticsAssetDetailDto>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Seller analytics asset detail cache hit: {Key}", cacheKey);
            return Result.Success(cached);
        }

        DateTimeOffset fromUtc = AnalyticsRange.ToUtcStart(request.From);
        DateTimeOffset toUtc = AnalyticsRange.ToUtcStart(request.To);
        AnalyticsGranularity granularity = AnalyticsRange.Granularity(request.From, request.To);
        AnalyticsAssetDetailSnapshot? snapshot = await analyticsStore.GetAssetDetail(
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

        IReadOnlyList<AnalyticsSeriesPoint> series = AnalyticsRange.BuildSeries(
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
            UsdAmount.FromDollarsRounded(snapshot.GrossRevenue, MidpointRounding.AwayFromZero).Cents,
            UsdAmount.FromDollarsRounded(snapshot.DirectRevenue, MidpointRounding.AwayFromZero).Cents,
            UsdAmount.FromDollarsRounded(snapshot.BundleAllocatedRevenue, MidpointRounding.AwayFromZero).Cents,
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
