using AssetBlock.Application.UseCases.SellerAnalytics;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;

namespace AssetBlock.Application.Tests.UseCases.SellerAnalytics;

public sealed class AnalyticsRangeTests
{

    [Fact]
    public void ComparisonPeriod_10Days_ReturnsCorrectPrecedingWindow()
    {
        var from = new DateOnly(2024, 1, 11);
        var to = new DateOnly(2024, 1, 21); // 10 days
        (DateOnly compFrom, DateOnly compTo) = AnalyticsRange.ComparisonPeriod(from, to);
        compFrom.Should().Be(new DateOnly(2024, 1, 1));
        compTo.Should().Be(new DateOnly(2024, 1, 11));
    }

    [Fact]
    public void ComparisonPeriod_1Day_ReturnsPreviousDay()
    {
        var from = new DateOnly(2024, 6, 15);
        var to = new DateOnly(2024, 6, 16);
        (DateOnly compFrom, DateOnly compTo) = AnalyticsRange.ComparisonPeriod(from, to);
        compFrom.Should().Be(new DateOnly(2024, 6, 14));
        compTo.Should().Be(new DateOnly(2024, 6, 15));
    }


    [Theory]
    [InlineData(1, AnalyticsGranularity.DAY)]
    [InlineData(45, AnalyticsGranularity.DAY)]
    [InlineData(46, AnalyticsGranularity.WEEK)]
    [InlineData(180, AnalyticsGranularity.WEEK)]
    [InlineData(181, AnalyticsGranularity.MONTH)]
    [InlineData(366, AnalyticsGranularity.MONTH)]
    public void Granularity_ByDayCount_ReturnsCorrectGranularity(int days, AnalyticsGranularity expected)
    {
        var from = new DateOnly(2024, 1, 1);
        var to = from.AddDays(days);
        AnalyticsRange.Granularity(from, to).Should().Be(expected);
    }


    [Fact]
    public void PercentageChange_WhenPreviousIsZero_ReturnsNull()
    {
        AnalyticsRange.PercentageChange(100m, 0m).Should().BeNull();
    }

    [Fact]
    public void PercentageChange_Increase_ReturnsPositive()
    {
        AnalyticsRange.PercentageChange(200m, 100m).Should().Be(100m);
    }

    [Fact]
    public void PercentageChange_Decrease_ReturnsNegative()
    {
        AnalyticsRange.PercentageChange(50m, 100m).Should().Be(-50m);
    }

    [Fact]
    public void PercentageChange_NoChange_ReturnsZero()
    {
        AnalyticsRange.PercentageChange(100m, 100m).Should().Be(0m);
    }


    [Fact]
    public void AovCents_ZeroOrders_ReturnsZero()
    {
        AnalyticsRange.AovCents(500m, 0).Should().Be(0);
    }

    [Fact]
    public void AovCents_EvenDivision_ReturnsCorrectCents()
    {
        AnalyticsRange.AovCents(30m, 3).Should().Be(1000); // $10.00
    }

    [Fact]
    public void AovCents_RoundsHalfCentUp()
    {
        // $10.005 per order; cents = 1000.5 → 1001
        AnalyticsRange.AovCents(20.01m, 2).Should().Be(1001);
    }

    [Fact]
    public void BuildSeries_Day_ZeroFillsMissingDays()
    {
        var from = new DateOnly(2024, 1, 1);
        var to = new DateOnly(2024, 1, 4); // 3 days: Jan 1, 2, 3
        var buckets = new[] { new AnalyticsDayBucket(new DateOnly(2024, 1, 2), 50m, 1, 1) };

        var series = AnalyticsRange.BuildSeries(buckets, from, to, AnalyticsGranularity.DAY);

        series.Should().HaveCount(3);
        series[0].GrossRevenueCents.Should().Be(0);   // Jan 1 - no data
        series[1].GrossRevenueCents.Should().Be(5000); // Jan 2 - $50
        series[2].GrossRevenueCents.Should().Be(0);   // Jan 3 - no data
    }

    [Fact]
    public void BuildSeries_Day_BucketStartIsUtcMidnight()
    {
        var from = new DateOnly(2024, 6, 15);
        var to = new DateOnly(2024, 6, 16);
        var series = AnalyticsRange.BuildSeries([], from, to, AnalyticsGranularity.DAY);

        series.Should().HaveCount(1);
        series[0].BucketStart.Should().Be(new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void BuildSeries_Week_StartsOnMonday()
    {
        var from = new DateOnly(2024, 1, 1); // Monday
        var to = new DateOnly(2024, 1, 15);  // 14 days = 2 weeks
        var series = AnalyticsRange.BuildSeries([], from, to, AnalyticsGranularity.WEEK);

        series.Should().HaveCount(2);
        series[0].BucketStart.DayOfWeek.Should().Be(DayOfWeek.Monday);
        series[1].BucketStart.DayOfWeek.Should().Be(DayOfWeek.Monday);
    }

    [Fact]
    public void BuildSeries_Week_AggregatesDaysIntoWeeks()
    {
        var from = new DateOnly(2024, 1, 1);
        var to = new DateOnly(2024, 1, 8);
        // Jan 1 = Monday, Jan 2 = Tuesday — both in same week
        var buckets = new[]
        {
            new AnalyticsDayBucket(new DateOnly(2024, 1, 1), 100m, 1, 1),
            new AnalyticsDayBucket(new DateOnly(2024, 1, 2), 200m, 2, 2)
        };

        var series = AnalyticsRange.BuildSeries(buckets, from, to, AnalyticsGranularity.WEEK);

        series.Should().HaveCount(1);
        series[0].GrossRevenueCents.Should().Be(30000); // $300
        series[0].Orders.Should().Be(3);
        series[0].UnitsSold.Should().Be(3);
    }

    [Fact]
    public void BuildSeries_Month_AggregatesDaysIntoMonths()
    {
        var from = new DateOnly(2024, 1, 1);
        var to = new DateOnly(2024, 3, 1); // Jan + Feb
        var buckets = new[]
        {
            new AnalyticsDayBucket(new DateOnly(2024, 1, 10), 100m, 1, 1),
            new AnalyticsDayBucket(new DateOnly(2024, 1, 20), 50m, 1, 1),
            new AnalyticsDayBucket(new DateOnly(2024, 2, 5), 75m, 1, 1)
        };

        var series = AnalyticsRange.BuildSeries(buckets, from, to, AnalyticsGranularity.MONTH);

        series.Should().HaveCount(2);
        series[0].GrossRevenueCents.Should().Be(15000); // Jan: $150
        series[1].GrossRevenueCents.Should().Be(7500);  // Feb: $75
    }
}
