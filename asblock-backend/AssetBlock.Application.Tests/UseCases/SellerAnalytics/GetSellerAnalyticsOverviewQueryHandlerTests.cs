using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.SellerAnalytics;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsOverview;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Analytics;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.SellerAnalytics;

public sealed class GetSellerAnalyticsOverviewQueryHandlerTests
{
    private readonly ISellerAnalyticsStore _store = Substitute.For<ISellerAnalyticsStore>();
    private readonly ITypedCache _cache = Substitute.For<ITypedCache>();
    private readonly GetSellerAnalyticsOverviewQueryHandler _handler;

    private static readonly Guid _sellerId = Guid.NewGuid();
    private static readonly DateOnly _from = new(2024, 1, 1);
    private static readonly DateOnly _to = new(2024, 1, 11);

    public GetSellerAnalyticsOverviewQueryHandlerTests()
    {
        _handler = new GetSellerAnalyticsOverviewQueryHandler(
            _store,
            _cache,
            NullLogger<GetSellerAnalyticsOverviewQueryHandler>.Instance);

        _cache.Get<SellerAnalyticsOverviewDto>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((SellerAnalyticsOverviewDto?)null);

        _store.GetOverviewSnapshot(
                Arg.Any<Guid>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<Domain.Core.Enums.AnalyticsGranularity>(),
                Arg.Any<CancellationToken>())
            .Returns(new SellerAnalyticsOverviewSnapshot(
                new SellerAnalyticsRawFacts(0, 0, 0, 0, 0, 0, 0, 0),
                new SellerAnalyticsRawFacts(0, 0, 0, 0, 0, 0, 0, 0),
                Array.Empty<AnalyticsDayBucket>(),
                Array.Empty<AnalyticsAssetProductRow>(),
                Array.Empty<AnalyticsBundleProductRow>(),
                new SellerRatingsRaw(null, 0),
                new SellerRatingsRaw(null, 0),
                null,
                null,
                null,
                null,
                new AnalyticsCommerceFunnelRaw(0, 0, 0, 0, 0),
                null,
                null,
                Array.Empty<AnalyticsTrafficSourceRaw>(),
                Array.Empty<AnalyticsExternalReferrerRaw>()));
    }

    [Fact]
    public async Task Handle_WhenCacheHit_ReturnsCachedResultWithoutStore()
    {
        var cachedDto = new SellerAnalyticsOverviewDto(
            _from, _to, _from.AddDays(-10), _from,
            Timezone: "UTC",
            Granularity: Domain.Core.Enums.AnalyticsGranularity.DAY,
            GeneratedAt: DateTimeOffset.UtcNow,
            Currency: "usd",
            EngagementAvailableFrom: null,
            GrossRevenue: new MoneyCentsMetric(0, 0, 0, null),
            DirectRevenue: new MoneyCentsMetric(0, 0, 0, null),
            BundleRevenue: new MoneyCentsMetric(0, 0, 0, null),
            Orders: new CountMetric(0, 0, 0, null),
            UnitsSold: new CountMetric(0, 0, 0, null),
            AverageOrderValue: new MoneyCentsMetric(0, 0, 0, null),
            UniqueCustomers: new CountMetric(0, 0, 0, null),
            NewCustomers: new CountMetric(0, 0, 0, null),
            ReturningCustomers: new CountMetric(0, 0, 0, null),
            RepeatCustomers: new CountMetric(0, 0, 0, null),
            RepeatCustomerRate: new RateMetric(null, null, null, null),
            AverageRating: null,
            NewReviews: new CountMetric(0, 0, 0, null),
            Series: [],
            TopAssets: [],
            TopBundles: [],
            EngagementTotals: null,
            CommerceFunnel: null,
            TrackedFunnel: null,
            TrackedCheckoutCoverage: null,
            TrafficSources: null);

        _cache.Get<SellerAnalyticsOverviewDto>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(cachedDto);

        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        await _store.DidNotReceiveWithAnyArgs().GetOverviewSnapshot(
            Guid.Empty, default, default, default, default, 0, default);
    }

    [Fact]
    public async Task Handle_AverageOrderValueCalculation_RoundsAwayFromZero()
    {
        _store.GetOverviewSnapshot(
                _sellerId,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<Domain.Core.Enums.AnalyticsGranularity>(),
                Arg.Any<CancellationToken>())
            .Returns(new SellerAnalyticsOverviewSnapshot(
                new SellerAnalyticsRawFacts(31.50m, 3, 3, 31.50m, 0, 2, 1, 0),
                new SellerAnalyticsRawFacts(0, 0, 0, 0, 0, 0, 0, 0),
                Array.Empty<AnalyticsDayBucket>(),
                Array.Empty<AnalyticsAssetProductRow>(),
                Array.Empty<AnalyticsBundleProductRow>(),
                new SellerRatingsRaw(null, 0),
                new SellerRatingsRaw(null, 0),
                null,
                null,
                null,
                null,
                new AnalyticsCommerceFunnelRaw(0, 0, 0, 0, 0),
                null,
                null,
                Array.Empty<AnalyticsTrafficSourceRaw>(),
                Array.Empty<AnalyticsExternalReferrerRaw>()));

        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AverageOrderValue.Current.Should().Be(1050L);
    }

    [Fact]
    public async Task Handle_AverageOrderValueCalculation_RoundsHalfCentUpward()
    {
        _store.GetOverviewSnapshot(
                _sellerId,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<Domain.Core.Enums.AnalyticsGranularity>(),
                Arg.Any<CancellationToken>())
            .Returns(new SellerAnalyticsOverviewSnapshot(
                new SellerAnalyticsRawFacts(20.01m, 2, 2, 20.01m, 0, 1, 1, 0),
                new SellerAnalyticsRawFacts(0, 0, 0, 0, 0, 0, 0, 0),
                Array.Empty<AnalyticsDayBucket>(),
                Array.Empty<AnalyticsAssetProductRow>(),
                Array.Empty<AnalyticsBundleProductRow>(),
                new SellerRatingsRaw(null, 0),
                new SellerRatingsRaw(null, 0),
                null,
                null,
                null,
                null,
                new AnalyticsCommerceFunnelRaw(0, 0, 0, 0, 0),
                null,
                null,
                Array.Empty<AnalyticsTrafficSourceRaw>(),
                Array.Empty<AnalyticsExternalReferrerRaw>()));

        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.AverageOrderValue.Current.Should().Be(1001L);
    }

    [Fact]
    public async Task Handle_PercentageChange_NullWhenPreviousIsZero()
    {
        _store.GetOverviewSnapshot(
                _sellerId,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<Domain.Core.Enums.AnalyticsGranularity>(),
                Arg.Any<CancellationToken>())
            .Returns(new SellerAnalyticsOverviewSnapshot(
                new SellerAnalyticsRawFacts(100m, 1, 1, 100m, 0, 1, 1, 0),
                new SellerAnalyticsRawFacts(0, 0, 0, 0, 0, 0, 0, 0),
                Array.Empty<AnalyticsDayBucket>(),
                Array.Empty<AnalyticsAssetProductRow>(),
                Array.Empty<AnalyticsBundleProductRow>(),
                new SellerRatingsRaw(null, 0),
                new SellerRatingsRaw(null, 0),
                null,
                null,
                null,
                null,
                new AnalyticsCommerceFunnelRaw(0, 0, 0, 0, 0),
                null,
                null,
                Array.Empty<AnalyticsTrafficSourceRaw>(),
                Array.Empty<AnalyticsExternalReferrerRaw>()));

        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GrossRevenue.PercentageChange.Should().BeNull();
    }

    [Fact]
    public async Task Handle_PercentageChange_ComputedCorrectly()
    {
        _store.GetOverviewSnapshot(
                _sellerId,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<Domain.Core.Enums.AnalyticsGranularity>(),
                Arg.Any<CancellationToken>())
            .Returns(new SellerAnalyticsOverviewSnapshot(
                new SellerAnalyticsRawFacts(200m, 2, 2, 200m, 0, 2, 0, 0),
                new SellerAnalyticsRawFacts(100m, 1, 1, 100m, 0, 1, 1, 0),
                Array.Empty<AnalyticsDayBucket>(),
                Array.Empty<AnalyticsAssetProductRow>(),
                Array.Empty<AnalyticsBundleProductRow>(),
                new SellerRatingsRaw(null, 0),
                new SellerRatingsRaw(null, 0),
                null,
                null,
                null,
                null,
                new AnalyticsCommerceFunnelRaw(0, 0, 0, 0, 0),
                null,
                null,
                Array.Empty<AnalyticsTrafficSourceRaw>(),
                Array.Empty<AnalyticsExternalReferrerRaw>()));

        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.GrossRevenue.PercentageChange.Should().Be(100m);
    }

    [Fact]
    public async Task Handle_ComparisonPeriod_IsCorrectPrecedingWindow()
    {
        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.ComparisonFrom.Should().Be(new DateOnly(2023, 12, 22));
        result.Value.ComparisonTo.Should().Be(new DateOnly(2024, 1, 1));
    }

    [Fact]
    public async Task Handle_ReturningCustomers_IsUniqueMinusNew()
    {
        _store.GetOverviewSnapshot(
                _sellerId,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<Domain.Core.Enums.AnalyticsGranularity>(),
                Arg.Any<CancellationToken>())
            .Returns(new SellerAnalyticsOverviewSnapshot(
                new SellerAnalyticsRawFacts(500m, 5, 5, 500m, 0, 5, 2, 1),
                new SellerAnalyticsRawFacts(0, 0, 0, 0, 0, 0, 0, 0),
                Array.Empty<AnalyticsDayBucket>(),
                Array.Empty<AnalyticsAssetProductRow>(),
                Array.Empty<AnalyticsBundleProductRow>(),
                new SellerRatingsRaw(null, 0),
                new SellerRatingsRaw(null, 0),
                null,
                null,
                null,
                null,
                new AnalyticsCommerceFunnelRaw(0, 0, 0, 0, 0),
                null,
                null,
                Array.Empty<AnalyticsTrafficSourceRaw>(),
                Array.Empty<AnalyticsExternalReferrerRaw>()));

        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.UniqueCustomers.Current.Should().Be(5);
        result.Value.NewCustomers.Current.Should().Be(2);
        result.Value.ReturningCustomers.Current.Should().Be(3);
        result.Value.RepeatCustomers.Current.Should().Be(1);
    }

    [Fact]
    public async Task Handle_GranularityDay_ForShortRange()
    {
        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Granularity.Should().Be(Domain.Core.Enums.AnalyticsGranularity.DAY);
    }

    [Fact]
    public async Task Handle_GranularityWeek_ForMediumRange()
    {
        var from = new DateOnly(2024, 1, 1);
        var to = new DateOnly(2024, 3, 1);
        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, from, to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Granularity.Should().Be(Domain.Core.Enums.AnalyticsGranularity.WEEK);
    }

    [Fact]
    public async Task Handle_SeriesZeroFilled_ForEmptyPeriod()
    {
        var from = new DateOnly(2024, 1, 1);
        var to = new DateOnly(2024, 1, 6);

        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, from, to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Series.Should().HaveCount(5);
        result.Value.Series.Should().AllSatisfy(p => p.GrossRevenueCents.Should().Be(0));
    }

    [Fact]
    public async Task Handle_CacheWriteCancelled_Rethrows()
    {
        _cache.Set(
                Arg.Any<string>(),
                Arg.Any<SellerAnalyticsOverviewDto>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => throw new OperationCanceledException());

        Func<Task<Result<SellerAnalyticsOverviewDto>>> act = () => _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Handle_WhenEngagementUnavailable_ShouldReturnNullEngagementBlocks()
    {
        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.EngagementAvailableFrom.Should().BeNull();
        result.Value.EngagementTotals.Should().BeNull();
        result.Value.TrackedFunnel.Should().BeNull();
        result.Value.CommerceFunnel.Should().NotBeNull();
        result.Value.TrafficSources.Should().NotBeNull();
    }

    [Fact]
    public async Task Handle_WhenEngagementAvailableWithZeroCounts_ShouldReturnZeroEngagementTotalsNotNull()
    {
        _store.GetOverviewSnapshot(
                _sellerId,
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<Domain.Core.Enums.AnalyticsGranularity>(),
                Arg.Any<CancellationToken>())
            .Returns(new SellerAnalyticsOverviewSnapshot(
                new SellerAnalyticsRawFacts(0, 0, 0, 0, 0, 0, 0, 0),
                new SellerAnalyticsRawFacts(0, 0, 0, 0, 0, 0, 0, 0),
                Array.Empty<AnalyticsDayBucket>(),
                Array.Empty<AnalyticsAssetProductRow>(),
                Array.Empty<AnalyticsBundleProductRow>(),
                new SellerRatingsRaw(null, 0),
                new SellerRatingsRaw(null, 0),
                EngagementAvailableFrom: DateTimeOffset.UtcNow.AddDays(-30),
                CurrentEngagement: new SellerEngagementRawFacts(0, 0, 0, 0, 0),
                ComparisonEngagement: new SellerEngagementRawFacts(0, 0, 0, 0, 0),
                EngagementDaySeries: Array.Empty<AnalyticsEngagementDayBucket>(),
                CommerceFunnel: new AnalyticsCommerceFunnelRaw(0, 0, 0, 0, 0),
                TrackedFunnel: new AnalyticsTrackedFunnelRaw(0, 0, 0),
                TrackedCheckoutCoverage: null,
                TrafficSources: Array.Empty<AnalyticsTrafficSourceRaw>(),
                ExternalReferrers: Array.Empty<AnalyticsExternalReferrerRaw>()));

        Result<SellerAnalyticsOverviewDto> result = await _handler.Handle(new GetSellerAnalyticsOverviewQuery(_sellerId, _from, _to), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.EngagementAvailableFrom.Should().NotBeNull();
        result.Value.EngagementTotals.Should().NotBeNull();
        result.Value.EngagementTotals!.ProductViews.Current.Should().Be(0);
        result.Value.TrackedFunnel.Should().NotBeNull();
        result.Value.TrackedFunnel!.ViewSessions.Should().Be(0);
    }
}

public sealed class SellerAnalyticsOverviewMapperTests
{
    [Fact]
    public void MapOverview_WhenZeroUniqueCustomers_RepeatCustomerRateIsNull()
    {
        var snapshot = new SellerAnalyticsOverviewSnapshot(
            new SellerAnalyticsRawFacts(0, 0, 0, 0, 0, 0, 0, 0),
            new SellerAnalyticsRawFacts(0, 0, 0, 0, 0, 0, 0, 0),
            Array.Empty<AnalyticsDayBucket>(),
            Array.Empty<AnalyticsAssetProductRow>(),
            Array.Empty<AnalyticsBundleProductRow>(),
            new SellerRatingsRaw(null, 0),
            new SellerRatingsRaw(null, 0),
            null, null, null, null,
            new AnalyticsCommerceFunnelRaw(0, 0, 0, 0, 0),
            null, null,
            Array.Empty<AnalyticsTrafficSourceRaw>(),
            Array.Empty<AnalyticsExternalReferrerRaw>());

        var from = new DateOnly(2024, 1, 1);
        var to = new DateOnly(2024, 1, 10);
        SellerAnalyticsOverviewDto dto = SellerAnalyticsOverviewMapper.MapOverview(
            snapshot, from, to, from.AddDays(-9), from, Domain.Core.Enums.AnalyticsGranularity.DAY);

        dto.RepeatCustomerRate.Current.Should().BeNull();
        dto.RepeatCustomerRate.Previous.Should().BeNull();
        dto.RepeatCustomerRate.AbsoluteChange.Should().BeNull();
        dto.RepeatCustomerRate.PercentageChange.Should().BeNull();
        dto.ReturningCustomers.Current.Should().Be(0);
    }

    [Fact]
    public void MapOverview_WithNonZeroMetrics_CalculatesCorrectPercentagesAndRates()
    {
        var curFacts = new SellerAnalyticsRawFacts(
            GrossRevenue: 1000m,
            Orders: 10,
            Units: 15,
            DirectRevenue: 600m,
            BundleRevenue: 400m,
            UniqueCustomers: 8,
            NewCustomers: 5,
            RepeatCustomers: 2);

        var prevFacts = new SellerAnalyticsRawFacts(
            GrossRevenue: 500m,
            Orders: 5,
            Units: 8,
            DirectRevenue: 300m,
            BundleRevenue: 200m,
            UniqueCustomers: 4,
            NewCustomers: 3,
            RepeatCustomers: 1);

        var snapshot = new SellerAnalyticsOverviewSnapshot(
            curFacts,
            prevFacts,
            Array.Empty<AnalyticsDayBucket>(),
            Array.Empty<AnalyticsAssetProductRow>(),
            Array.Empty<AnalyticsBundleProductRow>(),
            new SellerRatingsRaw(4.5, 3),
            new SellerRatingsRaw(4.0, 1),
            null, null, null, null,
            new AnalyticsCommerceFunnelRaw(10, 8, 5, 2, 1),
            null, null,
            Array.Empty<AnalyticsTrafficSourceRaw>(),
            Array.Empty<AnalyticsExternalReferrerRaw>());

        var from = new DateOnly(2024, 1, 1);
        var to = new DateOnly(2024, 1, 10);
        SellerAnalyticsOverviewDto dto = SellerAnalyticsOverviewMapper.MapOverview(
            snapshot, from, to, from.AddDays(-9), from, Domain.Core.Enums.AnalyticsGranularity.DAY);

        dto.GrossRevenue.Current.Should().Be(100000L);
        dto.GrossRevenue.Previous.Should().Be(50000L);
        dto.GrossRevenue.AbsoluteChange.Should().Be(50000L);
        dto.GrossRevenue.PercentageChange.Should().Be(100m);

        dto.Orders.Current.Should().Be(10);
        dto.Orders.Previous.Should().Be(5);
        dto.Orders.AbsoluteChange.Should().Be(5);
        dto.Orders.PercentageChange.Should().Be(100m);

        dto.ReturningCustomers.Current.Should().Be(3); // 8 - 5
        dto.ReturningCustomers.Previous.Should().Be(1); // 4 - 3

        dto.RepeatCustomerRate.Current.Should().Be(0.25m); // 2 / 8
        dto.RepeatCustomerRate.Previous.Should().Be(0.25m); // 1 / 4
        dto.RepeatCustomerRate.AbsoluteChange.Should().Be(0.0m);
        dto.RepeatCustomerRate.PercentageChange.Should().Be(0.0m);

        dto.AverageRating.Should().Be(4.5);
        dto.NewReviews.Current.Should().Be(3);
        dto.NewReviews.Previous.Should().Be(1);
    }
}
