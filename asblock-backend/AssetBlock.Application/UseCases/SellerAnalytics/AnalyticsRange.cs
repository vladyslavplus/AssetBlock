using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Payments;

namespace AssetBlock.Application.UseCases.SellerAnalytics;

/// <summary>
/// Static helpers for analytics period calculations, granularity, zero-fill, and metrics computation.
/// </summary>
public static class AnalyticsRange
{
    /// <summary>
    /// Returns the preceding equal-length calendar-day comparison period.
    /// E.g., 2024-01-10 → 2024-01-20 (10 days) → comparison is 2023-12-31 → 2024-01-10.
    /// </summary>
    public static (DateOnly From, DateOnly To) ComparisonPeriod(DateOnly from, DateOnly to)
    {
        var days = to.DayNumber - from.DayNumber;
        var compTo = from;
        var compFrom = from.AddDays(-days);
        return (compFrom, compTo);
    }

    /// <summary>Converts a DateOnly to a UTC DateTimeOffset at midnight.</summary>
    public static DateTimeOffset ToUtcStart(DateOnly date) =>
        new(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Determines granularity based on range length in calendar days.</summary>
    public static AnalyticsGranularity Granularity(DateOnly from, DateOnly to)
    {
        var days = to.DayNumber - from.DayNumber;
        return days switch
        {
            <= AnalyticsConstants.DAY_GRANULARITY_MAX_DAYS => AnalyticsGranularity.DAY,
            <= AnalyticsConstants.WEEK_GRANULARITY_MAX_DAYS => AnalyticsGranularity.WEEK,
            _ => AnalyticsGranularity.MONTH
        };
    }

    /// <summary>Returns null when previous == 0; otherwise % change rounded to 2 decimal places.</summary>
    public static decimal? PercentageChange(decimal current, decimal previous)
    {
        if (previous == 0)
        {
            return null;
        }

        return decimal.Round((current - previous) / Math.Abs(previous) * 100m, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>Average order value in cents. Zero orders → 0 cents. Uses MidpointRounding.AwayFromZero.</summary>
    public static long AovCents(decimal grossRevenue, int orders)
    {
        if (orders == 0)
        {
            return 0;
        }

        return UsdAmount.FromDollarsRounded(grossRevenue / orders, MidpointRounding.AwayFromZero).Cents;
    }

    /// <summary>
    /// Zero-fills commerce and engagement buckets for the requested granularity in the [from, to) range.
    /// Engagement buckets must already match <paramref name="granularity"/> (day/week/month starts).
    /// </summary>
    public static IReadOnlyList<AnalyticsSeriesPoint> BuildSeries(
        IReadOnlyList<AnalyticsDayBucket> dayBuckets,
        DateOnly from,
        DateOnly to,
        AnalyticsGranularity granularity,
        DateTimeOffset? engagementAvailableFrom,
        IReadOnlyList<AnalyticsEngagementDayBucket>? engagementBuckets = null)
    {
        return granularity switch
        {
            AnalyticsGranularity.DAY => BuildDaySeries(dayBuckets, from, to, engagementAvailableFrom, engagementBuckets),
            AnalyticsGranularity.WEEK => BuildWeekSeries(dayBuckets, from, to, engagementAvailableFrom, engagementBuckets),
            AnalyticsGranularity.MONTH => BuildMonthSeries(dayBuckets, from, to, engagementAvailableFrom, engagementBuckets),
            _ => BuildDaySeries(dayBuckets, from, to, engagementAvailableFrom, engagementBuckets)
        };
    }

    private static IReadOnlyList<AnalyticsSeriesPoint> BuildDaySeries(
        IReadOnlyList<AnalyticsDayBucket> dayBuckets,
        DateOnly from,
        DateOnly to,
        DateTimeOffset? engagementAvailableFrom,
        IReadOnlyList<AnalyticsEngagementDayBucket>? engagementBuckets)
    {
        var byDate = dayBuckets.ToDictionary(b => b.Date);
        var engagementByDate = engagementBuckets?.ToDictionary(b => b.Date);
        var result = new List<AnalyticsSeriesPoint>();
        for (var d = from; d < to; d = d.AddDays(1))
        {
            AnalyticsEngagementDayBucket? engagement = null;
            if (engagementByDate?.TryGetValue(d, out var found) == true)
            {
                engagement = found;
            }

            if (byDate.TryGetValue(d, out var commerce))
            {
                result.Add(CreateSeriesPoint(d, d.AddDays(1), commerce, engagement, engagementAvailableFrom));
            }
            else
            {
                result.Add(CreateSeriesPoint(d, d.AddDays(1), null, engagement, engagementAvailableFrom));
            }
        }

        return result;
    }

    private static IReadOnlyList<AnalyticsSeriesPoint> BuildWeekSeries(
        IReadOnlyList<AnalyticsDayBucket> dayBuckets,
        DateOnly from,
        DateOnly to,
        DateTimeOffset? engagementAvailableFrom,
        IReadOnlyList<AnalyticsEngagementDayBucket>? engagementBuckets)
    {
        var weekly = new Dictionary<DateOnly, (decimal Revenue, int Orders, int Units)>();
        foreach (var b in dayBuckets)
        {
            var monday = GetMondayOfWeek(b.Date);
            if (!weekly.TryGetValue(monday, out var existing))
            {
                weekly[monday] = (b.GrossRevenue, b.Orders, b.Units);
            }
            else
            {
                weekly[monday] = (existing.Revenue + b.GrossRevenue,
                    existing.Orders + b.Orders,
                    existing.Units + b.Units);
            }
        }

        var engagementByWeek = engagementBuckets?.ToDictionary(b => b.Date);
        var result = new List<AnalyticsSeriesPoint>();
        var weekStart = GetMondayOfWeek(from);
        while (weekStart < to)
        {
            AnalyticsDayBucket? commerceBucket = weekly.TryGetValue(weekStart, out var commerce)
                ? new AnalyticsDayBucket(weekStart, commerce.Revenue, commerce.Orders, commerce.Units)
                : null;
            AnalyticsEngagementDayBucket? engagement = null;
            if (engagementByWeek?.TryGetValue(weekStart, out var found) == true)
            {
                engagement = found;
            }

            result.Add(CreateSeriesPoint(
                weekStart,
                weekStart.AddDays(7),
                commerceBucket,
                engagement,
                engagementAvailableFrom));

            weekStart = weekStart.AddDays(7);
        }

        return result;
    }

    private static IReadOnlyList<AnalyticsSeriesPoint> BuildMonthSeries(
        IReadOnlyList<AnalyticsDayBucket> dayBuckets,
        DateOnly from,
        DateOnly to,
        DateTimeOffset? engagementAvailableFrom,
        IReadOnlyList<AnalyticsEngagementDayBucket>? engagementBuckets)
    {
        var monthly = new Dictionary<(int Year, int Month), (decimal Revenue, int Orders, int Units)>();
        foreach (var b in dayBuckets)
        {
            var key = (b.Date.Year, b.Date.Month);
            if (!monthly.TryGetValue(key, out var existing))
            {
                monthly[key] = (b.GrossRevenue, b.Orders, b.Units);
            }
            else
            {
                monthly[key] = (existing.Revenue + b.GrossRevenue,
                    existing.Orders + b.Orders,
                    existing.Units + b.Units);
            }
        }

        var engagementByMonth = engagementBuckets?.ToDictionary(b => (b.Date.Year, b.Date.Month));
        var result = new List<AnalyticsSeriesPoint>();
        var monthStart = new DateOnly(from.Year, from.Month, 1);
        while (monthStart < to)
        {
            var key = (monthStart.Year, monthStart.Month);
            monthly.TryGetValue(key, out var commerce);
            AnalyticsEngagementDayBucket? engagement = null;
            if (engagementByMonth?.TryGetValue(key, out var found) == true)
            {
                engagement = found;
            }

            result.Add(CreateSeriesPoint(
                monthStart,
                monthStart.AddMonths(1),
                monthly.ContainsKey(key)
                    ? new AnalyticsDayBucket(monthStart, commerce.Revenue, commerce.Orders, commerce.Units)
                    : null,
                engagement,
                engagementAvailableFrom));

            monthStart = monthStart.AddMonths(1);
        }

        return result;
    }

    private static AnalyticsSeriesPoint CreateSeriesPoint(
        DateOnly date,
        DateOnly bucketEndExclusive,
        AnalyticsDayBucket? commerce,
        AnalyticsEngagementDayBucket? engagement,
        DateTimeOffset? engagementAvailableFrom)
    {
        var checkoutStarts = engagement?.CheckoutStarts ?? 0;
        var completedOrders = engagement?.CompletedOrders ?? 0;

        long? productViews = null;
        long? uniqueVisitors = null;
        long? downloadRequests = null;

        if (engagementAvailableFrom.HasValue)
        {
            var availableDate = DateOnly.FromDateTime(engagementAvailableFrom.Value.UtcDateTime);
            // Null only when the whole bucket is strictly before availability; partial overlap → 0/values.
            if (availableDate < bucketEndExclusive)
            {
                productViews = engagement?.ProductViews ?? 0;
                uniqueVisitors = engagement?.UniqueVisitors ?? 0;
                downloadRequests = engagement?.DownloadRequests ?? 0;
            }
        }

        if (commerce is null)
        {
            return new AnalyticsSeriesPoint(
                ToUtcStart(date),
                0,
                0,
                0,
                productViews,
                uniqueVisitors,
                checkoutStarts,
                completedOrders,
                downloadRequests);
        }

        return new AnalyticsSeriesPoint(
            ToUtcStart(date),
            UsdAmount.FromDollarsRounded(commerce.GrossRevenue, MidpointRounding.AwayFromZero).Cents,
            commerce.Orders,
            commerce.Units,
            productViews,
            uniqueVisitors,
            checkoutStarts,
            completedOrders,
            downloadRequests);
    }

    /// <summary>Returns the Monday (UTC) of the ISO week containing <paramref name="date"/>.</summary>
    private static DateOnly GetMondayOfWeek(DateOnly date)
    {
        var dow = (int)date.DayOfWeek;
        var offset = dow == 0 ? 6 : dow - 1;
        return date.AddDays(-offset);
    }
}
