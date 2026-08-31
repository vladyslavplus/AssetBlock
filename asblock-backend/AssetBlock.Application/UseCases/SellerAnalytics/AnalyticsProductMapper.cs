using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Payments;

namespace AssetBlock.Application.UseCases.SellerAnalytics;

internal static class AnalyticsProductMapper
{
    public static AnalyticsProductItem FromAssetRow(AnalyticsAssetProductRow r) =>
        new(
            AnalyticsProductKind.ASSET,
            r.AssetId,
            r.Title,
            r.IsDeleted ? AnalyticsProductAvailability.UNAVAILABLE : AnalyticsProductAvailability.ACTIVE,
            UsdAmount.FromDollarsRounded(r.GrossRevenue, MidpointRounding.AwayFromZero).Cents,
            UsdAmount.FromDollarsRounded(r.DirectRevenue, MidpointRounding.AwayFromZero).Cents,
            UsdAmount.FromDollarsRounded(r.BundleAllocatedRevenue, MidpointRounding.AwayFromZero).Cents,
            r.Orders,
            r.UnitsSold,
            r.AverageRating,
            r.ReviewCount,
            r.LatestSaleAt,
            null, null, null);

    public static AnalyticsProductItem FromBundleRow(AnalyticsBundleProductRow r)
    {
        var (currentPriceCents, listPriceCents, discountPercent) =
            MapBundlePricing(r.CurrentPrice, r.ListPriceTotal);

        return new AnalyticsProductItem(
            AnalyticsProductKind.BUNDLE,
            r.BundleId,
            r.Title,
            r.IsArchived ? AnalyticsProductAvailability.ARCHIVED : AnalyticsProductAvailability.ACTIVE,
            UsdAmount.FromDollarsRounded(r.GrossRevenue, MidpointRounding.AwayFromZero).Cents,
            null, null,
            r.Orders,
            r.UnitsSold,
            null, null,
            r.LatestSaleAt,
            currentPriceCents,
            listPriceCents,
            discountPercent);
    }

    public static AnalyticsProductItem FromProductRow(AnalyticsProductRow r)
    {
        if (r.ProductKind == AnalyticsProductKind.ASSET)
        {
            return new AnalyticsProductItem(
                AnalyticsProductKind.ASSET,
                r.ProductId,
                r.Title,
                r.IsDeletedOrArchived
                    ? AnalyticsProductAvailability.UNAVAILABLE
                    : AnalyticsProductAvailability.ACTIVE,
                UsdAmount.FromDollarsRounded(r.GrossRevenue, MidpointRounding.AwayFromZero).Cents,
                UsdAmount.FromDollarsRounded(r.DirectRevenue, MidpointRounding.AwayFromZero).Cents,
                UsdAmount.FromDollarsRounded(r.BundleAllocatedRevenue, MidpointRounding.AwayFromZero).Cents,
                r.Orders,
                r.UnitsSold,
                r.AverageRating,
                r.ReviewCount,
                r.LatestSaleAt,
                null, null, null);
        }

        var (currentPriceCents, listPriceCents, discountPercent) =
            MapBundlePricing(r.CurrentPrice, r.ListPriceTotal);

        return new AnalyticsProductItem(
            AnalyticsProductKind.BUNDLE,
            r.ProductId,
            r.Title,
            r.IsDeletedOrArchived
                ? AnalyticsProductAvailability.ARCHIVED
                : AnalyticsProductAvailability.ACTIVE,
            UsdAmount.FromDollarsRounded(r.GrossRevenue, MidpointRounding.AwayFromZero).Cents,
            null, null,
            r.Orders,
            r.UnitsSold,
            null, null,
            r.LatestSaleAt,
            currentPriceCents,
            listPriceCents,
            discountPercent);
    }

    private static (long? CurrentPriceCents, long? ListPriceCents, decimal? DiscountPercent) MapBundlePricing(
        decimal? currentPrice,
        decimal? listPrice)
    {
        long? currentPriceCents = currentPrice.HasValue
            ? UsdAmount.FromDollarsRounded(currentPrice.Value, MidpointRounding.AwayFromZero).Cents
            : null;
        long? listPriceCents = listPrice.HasValue
            ? UsdAmount.FromDollarsRounded(listPrice.Value, MidpointRounding.AwayFromZero).Cents
            : null;
        decimal? discountPercent = null;
        if (currentPriceCents.HasValue && listPriceCents is > 0)
        {
            discountPercent = decimal.Round(
                (1m - (decimal)currentPriceCents.Value / listPriceCents.Value) * 100m,
                2, MidpointRounding.AwayFromZero);
        }

        return (currentPriceCents, listPriceCents, discountPercent);
    }

    internal static (long? CurrentPriceCents, long? ListPriceCents, decimal? DiscountPercent) MapBundlePricingPublic(
        decimal? currentPrice,
        decimal? listPrice) => MapBundlePricing(currentPrice, listPrice);
}

internal static class SellerAnalyticsOverviewMapper
{
    public static SellerAnalyticsOverviewDto MapOverview(
        SellerAnalyticsOverviewSnapshot snapshot,
        DateOnly from,
        DateOnly to,
        DateOnly compFrom,
        DateOnly compTo,
        AnalyticsGranularity granularity)
    {
        var cur = snapshot.CurrentFacts;
        var prev = snapshot.ComparisonFacts;
        var ratings = snapshot.CurrentRatings;
        var prevRatings = snapshot.ComparisonRatings;

        var series = AnalyticsRange.BuildSeries(
            snapshot.DaySeries,
            from,
            to,
            granularity,
            snapshot.EngagementAvailableFrom,
            snapshot.EngagementDaySeries);

        var curRevCents = UsdAmount.FromDollarsRounded(cur.GrossRevenue, MidpointRounding.AwayFromZero).Cents;
        var prevRevCents = UsdAmount.FromDollarsRounded(prev.GrossRevenue, MidpointRounding.AwayFromZero).Cents;
        var curDirCents = UsdAmount.FromDollarsRounded(cur.DirectRevenue, MidpointRounding.AwayFromZero).Cents;
        var prevDirCents = UsdAmount.FromDollarsRounded(prev.DirectRevenue, MidpointRounding.AwayFromZero).Cents;
        var curBundleCents = UsdAmount.FromDollarsRounded(cur.BundleRevenue, MidpointRounding.AwayFromZero).Cents;
        var prevBundleCents = UsdAmount.FromDollarsRounded(prev.BundleRevenue, MidpointRounding.AwayFromZero).Cents;
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

        return new SellerAnalyticsOverviewDto(
            From: from,
            To: to,
            ComparisonFrom: compFrom,
            ComparisonTo: compTo,
            Timezone: "UTC",
            Granularity: granularity,
            GeneratedAt: DateTimeOffset.UtcNow,
            Currency: Domain.Core.Constants.AnalyticsConstants.CURRENCY,
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
    }
}
