using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsOverview;

internal sealed class GetSellerAnalyticsOverviewQueryHandler(
    ISellerAnalyticsStore analyticsStore,
    ITypedCache cache,
    ILogger<GetSellerAnalyticsOverviewQueryHandler> logger)
    : IRequestHandler<GetSellerAnalyticsOverviewQuery, Result<SellerAnalyticsOverviewDto>>
{
    private static readonly TimeSpan _cacheExpiration =
        TimeSpan.FromSeconds(AnalyticsConstants.OVERVIEW_CACHE_TTL_SECONDS);

    public async Task<Result<SellerAnalyticsOverviewDto>> Handle(
        GetSellerAnalyticsOverviewQuery request,
        CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.SellerAnalyticsOverview(request.SellerId, request.From, request.To);

        var cached = await cache.Get<SellerAnalyticsOverviewDto>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Seller analytics overview cache hit: {Key}", cacheKey);
            return Result.Success(cached);
        }

        var fromDto = AnalyticsRange.ToUtcStart(request.From);
        var toDto = AnalyticsRange.ToUtcStart(request.To);

        (DateOnly compFrom, DateOnly compTo) = AnalyticsRange.ComparisonPeriod(request.From, request.To);
        var compFromDto = AnalyticsRange.ToUtcStart(compFrom);
        var compToDto = AnalyticsRange.ToUtcStart(compTo);

        var granularity = AnalyticsRange.Granularity(request.From, request.To);

        var snapshot = await analyticsStore.GetOverviewSnapshot(
            request.SellerId,
            fromDto,
            toDto,
            compFromDto,
            compToDto,
            AnalyticsConstants.OVERVIEW_TOP_N,
            granularity,
            cancellationToken);

        var cur = snapshot.CurrentFacts;
        var prev = snapshot.ComparisonFacts;
        var ratings = snapshot.CurrentRatings;
        var prevRatings = snapshot.ComparisonRatings;

        var series = AnalyticsRange.BuildSeries(
            snapshot.DaySeries,
            request.From,
            request.To,
            granularity,
            snapshot.EngagementAvailableFrom,
            snapshot.EngagementDaySeries);

        var curRevCents = AnalyticsRange.ToCents(cur.GrossRevenue);
        var prevRevCents = AnalyticsRange.ToCents(prev.GrossRevenue);
        var curDirCents = AnalyticsRange.ToCents(cur.DirectRevenue);
        var prevDirCents = AnalyticsRange.ToCents(prev.DirectRevenue);
        var curBundleCents = AnalyticsRange.ToCents(cur.BundleRevenue);
        var prevBundleCents = AnalyticsRange.ToCents(prev.BundleRevenue);
        var curAov = AnalyticsRange.AovCents(cur.GrossRevenue, cur.Orders);
        var prevAov = AnalyticsRange.AovCents(prev.GrossRevenue, prev.Orders);
        var curReturning = cur.UniqueCustomers - cur.NewCustomers;
        var prevReturning = prev.UniqueCustomers - prev.NewCustomers;
        var curRepeatRate = cur.UniqueCustomers > 0
            ? (decimal)cur.RepeatCustomers / cur.UniqueCustomers
            : (decimal?)null;
        var prevRepeatRate = prev.UniqueCustomers > 0
            ? (decimal)prev.RepeatCustomers / prev.UniqueCustomers
            : (decimal?)null;
        var repeatRateAbs = curRepeatRate.HasValue && prevRepeatRate.HasValue
            ? curRepeatRate.Value - prevRepeatRate.Value
            : (decimal?)null;

        var engagementAvailable = snapshot.EngagementAvailableFrom;
        var engagementTotals = AnalyticsEngagementMapper.MapEngagementTotals(
            snapshot.CurrentEngagement,
            snapshot.ComparisonEngagement);
        var commerceFunnel = AnalyticsEngagementMapper.MapCommerceFunnel(snapshot.CommerceFunnel);
        var trackedFunnel = AnalyticsEngagementMapper.MapTrackedFunnel(snapshot.TrackedFunnel);
        var trafficSources = AnalyticsEngagementMapper.MapTrafficSources(
            snapshot.TrafficSources,
            snapshot.ExternalReferrers);

        var overviewDto = new SellerAnalyticsOverviewDto(
            From: request.From,
            To: request.To,
            ComparisonFrom: compFrom,
            ComparisonTo: compTo,
            Timezone: "UTC",
            Granularity: granularity,
            GeneratedAt: DateTimeOffset.UtcNow,
            Currency: AnalyticsConstants.CURRENCY,
            EngagementAvailableFrom: engagementAvailable,
            GrossRevenue: new MoneyCentsMetric(curRevCents, prevRevCents, curRevCents - prevRevCents,
                AnalyticsRange.PercentageChange(curRevCents, prevRevCents)),
            DirectRevenue: new MoneyCentsMetric(curDirCents, prevDirCents, curDirCents - prevDirCents,
                AnalyticsRange.PercentageChange(curDirCents, prevDirCents)),
            BundleRevenue: new MoneyCentsMetric(curBundleCents, prevBundleCents, curBundleCents - prevBundleCents,
                AnalyticsRange.PercentageChange(curBundleCents, prevBundleCents)),
            Orders: new CountMetric(cur.Orders, prev.Orders, cur.Orders - prev.Orders,
                AnalyticsRange.PercentageChange(cur.Orders, prev.Orders)),
            UnitsSold: new CountMetric(cur.Units, prev.Units, cur.Units - prev.Units,
                AnalyticsRange.PercentageChange(cur.Units, prev.Units)),
            AverageOrderValue: new MoneyCentsMetric(curAov, prevAov, curAov - prevAov,
                AnalyticsRange.PercentageChange(curAov, prevAov)),
            UniqueCustomers: new CountMetric(cur.UniqueCustomers, prev.UniqueCustomers,
                cur.UniqueCustomers - prev.UniqueCustomers,
                AnalyticsRange.PercentageChange(cur.UniqueCustomers, prev.UniqueCustomers)),
            NewCustomers: new CountMetric(cur.NewCustomers, prev.NewCustomers,
                cur.NewCustomers - prev.NewCustomers,
                AnalyticsRange.PercentageChange(cur.NewCustomers, prev.NewCustomers)),
            ReturningCustomers: new CountMetric(curReturning, prevReturning,
                curReturning - prevReturning,
                AnalyticsRange.PercentageChange(curReturning, prevReturning)),
            RepeatCustomers: new CountMetric(cur.RepeatCustomers, prev.RepeatCustomers,
                cur.RepeatCustomers - prev.RepeatCustomers,
                AnalyticsRange.PercentageChange(cur.RepeatCustomers, prev.RepeatCustomers)),
            RepeatCustomerRate: new RateMetric(
                curRepeatRate,
                prevRepeatRate,
                repeatRateAbs,
                curRepeatRate.HasValue && prevRepeatRate.HasValue
                    ? AnalyticsRange.PercentageChange(curRepeatRate.Value, prevRepeatRate.Value)
                    : null),
            AverageRating: ratings.AverageRating,
            NewReviews: new CountMetric(ratings.NewReviews, prevRatings.NewReviews,
                ratings.NewReviews - prevRatings.NewReviews,
                AnalyticsRange.PercentageChange(ratings.NewReviews, prevRatings.NewReviews)),
            Series: series,
            TopAssets: snapshot.TopAssets.Select(AnalyticsProductMapper.FromAssetRow).ToList(),
            TopBundles: snapshot.TopBundles.Select(AnalyticsProductMapper.FromBundleRow).ToList(),
            EngagementTotals: engagementTotals,
            CommerceFunnel: commerceFunnel,
            TrackedFunnel: trackedFunnel,
            TrackedCheckoutCoverage: snapshot.TrackedCheckoutCoverage,
            TrafficSources: trafficSources);

        await cache.Set(cacheKey, overviewDto, _cacheExpiration, cancellationToken);

        return Result.Success(overviewDto);
    }
}
