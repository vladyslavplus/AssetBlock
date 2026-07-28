using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.SellerAnalytics;

/// <summary>
/// Static helpers for analytics period calculations, granularity, zero-fill, and metrics computation.
/// </summary>
internal static class AnalyticsRange
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

    /// <summary>Converts decimal dollars to integer cents (MidpointRounding.AwayFromZero).</summary>
    public static long ToCents(decimal amount) =>
        (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>Average order value in cents. Zero orders → 0 cents. Uses MidpointRounding.AwayFromZero.</summary>
    public static long AovCents(decimal grossRevenue, int orders)
    {
        if (orders == 0)
        {
            return 0;
        }

        return (long)decimal.Round(grossRevenue * 100m / orders, 0, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Aggregates per-day buckets into weekly or monthly buckets, then zero-fills all missing
    /// buckets for the requested granularity in the [from, to) range.
    /// </summary>
    public static IReadOnlyList<AnalyticsSeriesPoint> BuildSeries(
        IReadOnlyList<AnalyticsDayBucket> dayBuckets,
        DateOnly from,
        DateOnly to,
        AnalyticsGranularity granularity)
    {
        return granularity switch
        {
            AnalyticsGranularity.DAY => BuildDaySeries(dayBuckets, from, to),
            AnalyticsGranularity.WEEK => BuildWeekSeries(dayBuckets, from, to),
            AnalyticsGranularity.MONTH => BuildMonthSeries(dayBuckets, from, to),
            _ => BuildDaySeries(dayBuckets, from, to)
        };
    }

    private static IReadOnlyList<AnalyticsSeriesPoint> BuildDaySeries(
        IReadOnlyList<AnalyticsDayBucket> dayBuckets, DateOnly from, DateOnly to)
    {
        var byDate = dayBuckets.ToDictionary(b => b.Date);
        var result = new List<AnalyticsSeriesPoint>();
        for (var d = from; d < to; d = d.AddDays(1))
        {
            result.Add(byDate.TryGetValue(d, out var b)
                ? new AnalyticsSeriesPoint(ToUtcStart(d), ToCents(b.GrossRevenue), b.Orders, b.Units)
                : new AnalyticsSeriesPoint(ToUtcStart(d), 0, 0, 0));
        }

        return result;
    }

    private static IReadOnlyList<AnalyticsSeriesPoint> BuildWeekSeries(
        IReadOnlyList<AnalyticsDayBucket> dayBuckets, DateOnly from, DateOnly to)
    {
        // Aggregate days into ISO weeks (week starts on Monday)
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

        // Generate week buckets that overlap [from, to)
        var result = new List<AnalyticsSeriesPoint>();
        var weekStart = GetMondayOfWeek(from);
        while (weekStart < to)
        {
            result.Add(weekly.TryGetValue(weekStart, out var w)
                ? new AnalyticsSeriesPoint(ToUtcStart(weekStart), ToCents(w.Revenue), w.Orders, w.Units)
                : new AnalyticsSeriesPoint(ToUtcStart(weekStart), 0, 0, 0));

            weekStart = weekStart.AddDays(7);
        }

        return result;
    }

    private static IReadOnlyList<AnalyticsSeriesPoint> BuildMonthSeries(
        IReadOnlyList<AnalyticsDayBucket> dayBuckets, DateOnly from, DateOnly to)
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

        var result = new List<AnalyticsSeriesPoint>();
        var monthStart = new DateOnly(from.Year, from.Month, 1);
        while (monthStart < to)
        {
            var key = (monthStart.Year, monthStart.Month);
            result.Add(monthly.TryGetValue(key, out var m)
                ? new AnalyticsSeriesPoint(ToUtcStart(monthStart), ToCents(m.Revenue), m.Orders, m.Units)
                : new AnalyticsSeriesPoint(ToUtcStart(monthStart), 0, 0, 0));

            monthStart = monthStart.AddMonths(1);
        }

        return result;
    }

    /// <summary>Returns the Monday (UTC) of the ISO week containing <paramref name="date"/>.</summary>
    private static DateOnly GetMondayOfWeek(DateOnly date)
    {
        var dow = (int)date.DayOfWeek;
        // Sunday = 0; Monday = 1; ...; Saturday = 6
        var offset = dow == 0 ? 6 : dow - 1;
        return date.AddDays(-offset);
    }
}
