using AssetBlock.Domain.Core.Dto.Analytics;

namespace AssetBlock.Application.UseCases.SellerAnalytics;

internal static class AnalyticsEngagementMapper
{
    public static AnalyticsEngagementTotals? MapEngagementTotals(
        SellerEngagementRawFacts? current,
        SellerEngagementRawFacts? comparison)
    {
        if (current is null)
        {
            return null;
        }

        return new AnalyticsEngagementTotals(
            BuildEngagementCountMetric(current.ProductViews, comparison?.ProductViews),
            BuildEngagementCountMetric(current.UniqueVisitors, comparison?.UniqueVisitors),
            BuildEngagementCountMetric(current.DownloadRequests, comparison?.DownloadRequests),
            BuildEngagementCountMetric(current.CollectionViews, comparison?.CollectionViews),
            BuildEngagementCountMetric(current.CollectionItemClicks, comparison?.CollectionItemClicks));
    }

    public static AnalyticsCommerceFunnel? MapCommerceFunnel(AnalyticsCommerceFunnelRaw? raw)
    {
        if (raw is null)
        {
            return null;
        }

        decimal? completionRate = raw.CheckoutStarts > 0
            ? decimal.Round((decimal)raw.CompletedOrders / raw.CheckoutStarts, 4, MidpointRounding.AwayFromZero)
            : null;

        var terminalDenominator = raw.CompletedOrders + raw.CancelledCheckouts;
        decimal? abandonmentRate = terminalDenominator > 0
            ? decimal.Round((decimal)raw.CancelledCheckouts / terminalDenominator, 4, MidpointRounding.AwayFromZero)
            : null;

        return new AnalyticsCommerceFunnel(
            raw.CheckoutStarts,
            raw.StripeSessionsAttached,
            raw.CompletedOrders,
            raw.CancelledCheckouts,
            raw.PendingCheckouts,
            completionRate,
            abandonmentRate);
    }

    public static AnalyticsTrackedFunnel? MapTrackedFunnel(AnalyticsTrackedFunnelRaw? raw)
    {
        if (raw is null)
        {
            return null;
        }

        return new AnalyticsTrackedFunnel(
            raw.ViewSessions,
            raw.CheckoutSessions,
            raw.CompletedSessions,
            Rate(raw.CheckoutSessions, raw.ViewSessions),
            Rate(raw.CompletedSessions, raw.CheckoutSessions),
            Rate(raw.CompletedSessions, raw.ViewSessions));
    }

    public static IReadOnlyList<AnalyticsTrafficSourceRow>? MapTrafficSources(
        IReadOnlyList<AnalyticsTrafficSourceRaw>? sources,
        IReadOnlyList<AnalyticsExternalReferrerRaw>? externalReferrers)
    {
        if (sources is null)
        {
            return null;
        }

        var externalRows = externalReferrers?
            .Select(r => new AnalyticsExternalReferrerRow(
                r.ReferrerHost,
                r.ProductViews,
                r.UniqueVisitors,
                r.CheckoutStarts,
                r.CompletedOrders,
                AnalyticsRange.ToCents(r.AttributedGrossRevenue)))
            .ToList();

        return sources.Select(s => new AnalyticsTrafficSourceRow(
            s.Source,
            s.ProductViews,
            s.UniqueVisitors,
            s.CheckoutStarts,
            s.CompletedOrders,
            AnalyticsRange.ToCents(s.AttributedGrossRevenue),
            s.Source == Domain.Core.Enums.AnalyticsTrafficSource.EXTERNAL ? externalRows : null)).ToList();
    }

    public static decimal? TrackedViewToCheckoutRate(int? checkoutSessions, int? viewSessions)
    {
        if (!checkoutSessions.HasValue || !viewSessions.HasValue)
        {
            return null;
        }

        return Rate(checkoutSessions.Value, viewSessions.Value);
    }

    public static decimal? CheckoutCompletionRate(int? completedCheckouts, int? checkoutStarts)
    {
        if (!completedCheckouts.HasValue || !checkoutStarts.HasValue)
        {
            return null;
        }

        return Rate(completedCheckouts.Value, checkoutStarts.Value);
    }

    private static EngagementCountMetric BuildEngagementCountMetric(long current, long? previous) =>
        previous.HasValue
            ? new EngagementCountMetric(
                current,
                previous.Value,
                current - previous.Value,
                AnalyticsRange.PercentageChange(current, previous.Value))
            : new EngagementCountMetric(current, null, null, null);

    private static decimal? Rate(long numerator, long denominator)
    {
        if (denominator <= 0)
        {
            return null;
        }

        return decimal.Round((decimal)numerator / denominator, 4, MidpointRounding.AwayFromZero);
    }
}
