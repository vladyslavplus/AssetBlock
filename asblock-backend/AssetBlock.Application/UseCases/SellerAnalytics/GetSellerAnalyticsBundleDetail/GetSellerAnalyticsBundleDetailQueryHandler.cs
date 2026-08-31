using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Payments;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsBundleDetail;

internal sealed class GetSellerAnalyticsBundleDetailQueryHandler(
    ISellerAnalyticsStore analyticsStore,
    ITypedCache cache,
    ILogger<GetSellerAnalyticsBundleDetailQueryHandler> logger)
    : IRequestHandler<GetSellerAnalyticsBundleDetailQuery, Result<AnalyticsBundleDetailDto>>
{
    private static readonly TimeSpan _cacheExpiration =
        TimeSpan.FromSeconds(AnalyticsConstants.PRODUCT_DETAIL_CACHE_TTL_SECONDS);

    public async Task<Result<AnalyticsBundleDetailDto>> Handle(
        GetSellerAnalyticsBundleDetailQuery request,
        CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.SellerAnalyticsBundleDetail(
            request.SellerId,
            request.BundleId,
            request.From,
            request.To);

        var cached = await cache.Get<AnalyticsBundleDetailDto>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Seller analytics bundle detail cache hit: {Key}", cacheKey);
            return Result.Success(cached);
        }

        var fromUtc = AnalyticsRange.ToUtcStart(request.From);
        var toUtc = AnalyticsRange.ToUtcStart(request.To);
        var granularity = AnalyticsRange.Granularity(request.From, request.To);
        var snapshot = await analyticsStore.GetBundleDetail(
            request.SellerId,
            request.BundleId,
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

        var (currentPriceCents, listPriceCents, discountPercent) =
            AnalyticsProductMapper.MapBundlePricingPublic(snapshot.CurrentPrice, snapshot.ListPriceTotal);

        var dto = new AnalyticsBundleDetailDto(
            request.From,
            request.To,
            "UTC",
            DateTimeOffset.UtcNow,
            AnalyticsConstants.CURRENCY,
            granularity,
            snapshot.EngagementAvailableFrom,
            snapshot.BundleId,
            snapshot.Title,
            snapshot.IsArchived
                ? AnalyticsProductAvailability.ARCHIVED
                : AnalyticsProductAvailability.ACTIVE,
            UsdAmount.FromDollarsRounded(snapshot.GrossRevenue, MidpointRounding.AwayFromZero).Cents,
            snapshot.Orders,
            snapshot.UnitsSold,
            currentPriceCents,
            listPriceCents,
            discountPercent,
            snapshot.LatestSaleAt,
            snapshot.CheckoutStarts ?? 0,
            snapshot.ProductViews,
            snapshot.UniqueVisitors,
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
