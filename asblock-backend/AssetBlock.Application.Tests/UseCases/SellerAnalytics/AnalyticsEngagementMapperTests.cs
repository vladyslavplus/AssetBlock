using AssetBlock.Application.UseCases.SellerAnalytics;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;

namespace AssetBlock.Application.Tests.UseCases.SellerAnalytics;

public sealed class AnalyticsEngagementMapperTests
{
    [Fact]
    public void MapEngagementTotals_WhenCurrentMissing_ShouldReturnNull()
    {
        var comparison = new SellerEngagementRawFacts(10, 5, 1, 2, 3);

        AnalyticsEngagementMapper.MapEngagementTotals(null, comparison).Should().BeNull();
    }

    [Fact]
    public void MapEngagementTotals_WhenComparisonMissing_ShouldReturnCurrentWithNullComparisonFields()
    {
        var current = new SellerEngagementRawFacts(10, 5, 1, 2, 3);

        var totals = AnalyticsEngagementMapper.MapEngagementTotals(current, null);

        totals.Should().NotBeNull();
        totals.ProductViews.Current.Should().Be(10);
        totals.ProductViews.Previous.Should().BeNull();
        totals.ProductViews.AbsoluteChange.Should().BeNull();
        totals.ProductViews.PercentageChange.Should().BeNull();
    }

    [Fact]
    public void MapEngagementTotals_WhenBothPresent_ShouldComputeFullMetrics()
    {
        var current = new SellerEngagementRawFacts(10, 5, 1, 2, 3);
        var comparison = new SellerEngagementRawFacts(5, 2, 0, 1, 1);

        var totals = AnalyticsEngagementMapper.MapEngagementTotals(current, comparison);

        totals.Should().NotBeNull();
        totals.ProductViews.Current.Should().Be(10);
        totals.ProductViews.Previous.Should().Be(5);
        totals.ProductViews.AbsoluteChange.Should().Be(5);
        totals.ProductViews.PercentageChange.Should().Be(100m);
    }

    [Fact]
    public void MapCommerceFunnel_WhenCheckoutStartsZero_ShouldReturnNullCompletionRate()
    {
        var funnel = AnalyticsEngagementMapper.MapCommerceFunnel(
            new AnalyticsCommerceFunnelRaw(0, 0, 0, 0, 0));

        funnel.Should().NotBeNull();
        funnel.CheckoutCompletionRate.Should().BeNull();
        funnel.TerminalAbandonmentRate.Should().BeNull();
    }

    [Fact]
    public void MapTrackedFunnel_WhenDenominatorZero_ShouldReturnNullRates()
    {
        var funnel = AnalyticsEngagementMapper.MapTrackedFunnel(
            new AnalyticsTrackedFunnelRaw(0, 0, 0));

        funnel.Should().NotBeNull();
        funnel.ViewToCheckoutRate.Should().BeNull();
        funnel.CheckoutToCompletedRate.Should().BeNull();
        funnel.ViewToCompletedRate.Should().BeNull();
    }

    [Fact]
    public void TrackedViewToCheckoutRate_WhenEitherInputNull_ShouldReturnNull()
    {
        AnalyticsEngagementMapper.TrackedViewToCheckoutRate(null, 5).Should().BeNull();
        AnalyticsEngagementMapper.TrackedViewToCheckoutRate(5, null).Should().BeNull();
    }

    [Fact]
    public void CheckoutCompletionRate_WhenDenominatorZero_ShouldReturnNull()
    {
        AnalyticsEngagementMapper.CheckoutCompletionRate(0, 0).Should().BeNull();
        AnalyticsEngagementMapper.CheckoutCompletionRate(1, 0).Should().BeNull();
    }

    [Fact]
    public void MapTrackedFunnel_WhenCountsPresent_ShouldComputeRoundedRates()
    {
        var funnel = AnalyticsEngagementMapper.MapTrackedFunnel(
            new AnalyticsTrackedFunnelRaw(100, 25, 5));

        funnel!.ViewToCheckoutRate.Should().Be(0.25m);
        funnel.CheckoutToCompletedRate.Should().Be(0.2m);
        funnel.ViewToCompletedRate.Should().Be(0.05m);
    }

    [Fact]
    public void MapTrafficSources_WhenSourcesNull_ShouldReturnNull()
    {
        AnalyticsEngagementMapper.MapTrafficSources(null, []).Should().BeNull();
    }

    [Fact]
    public void MapTrafficSources_WhenExternalSourcePresent_ShouldAttachReferrerRows()
    {
        var rows = AnalyticsEngagementMapper.MapTrafficSources(
            [new AnalyticsTrafficSourceRaw(AnalyticsTrafficSource.EXTERNAL, 10, 4, 2, 1, 9.99m)],
            [new AnalyticsExternalReferrerRaw("news.example", 10, 4, 2, 1, 9.99m)]);

        rows.Should().NotBeNull();
        rows[0].ExternalReferrers.Should().NotBeNull();
        rows[0].ExternalReferrers![0].AttributedGrossRevenueCents.Should().Be(999L);
    }
}
