using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsProducts;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.SellerAnalytics;

public sealed class GetSellerAnalyticsProductsQueryHandlerTests
{
    private readonly ISellerAnalyticsStore _store = Substitute.For<ISellerAnalyticsStore>();
    private readonly ITypedCache _cache = Substitute.For<ITypedCache>();
    private readonly GetSellerAnalyticsProductsQueryHandler _handler;

    public GetSellerAnalyticsProductsQueryHandlerTests()
    {
        _handler = new GetSellerAnalyticsProductsQueryHandler(
            _store,
            _cache,
            NullLogger<GetSellerAnalyticsProductsQueryHandler>.Instance);

        _cache.Get<AnalyticsProductsResult>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AnalyticsProductsResult?)null);
    }

    [Fact]
    public async Task Handle_WhenStoreReturnsRows_MapsToAnalyticsProductItem()
    {
        var assetId = Guid.NewGuid();
        _store.GetProductsPage(
                Arg.Any<Guid>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                AnalyticsProductTypeFilter.ALL,
                1,
                20,
                AnalyticsProductSort.REVENUE,
                AnalyticsSortDirection.DESC,
                Arg.Any<CancellationToken>())
            .Returns((
                new List<AnalyticsProductRow>
                {
                    new(
                        AnalyticsProductKind.ASSET,
                        assetId,
                        "Test Asset",
                        false,
                        10m,
                        10m,
                        0m,
                        1,
                        1,
                        4.5,
                        2,
                        DateTimeOffset.UtcNow,
                        null,
                        null)
                },
                1));

        var req = new AnalyticsProductsRequest(new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 1));
        var result = await _handler.Handle(new GetSellerAnalyticsProductsQuery(Guid.NewGuid(), req), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].ProductKind.Should().Be(AnalyticsProductKind.ASSET);
        result.Value.Items[0].GrossRevenueCents.Should().Be(1000);
        result.Value.From.Should().Be(req.From);
        result.Value.Timezone.Should().Be("UTC");
        result.Value.Currency.Should().Be(AnalyticsConstants.CURRENCY);
    }

    [Fact]
    public async Task Handle_UsesRealPageSize_NotMaxValue()
    {
        var req = new AnalyticsProductsRequest(new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 1), Page: 2, PageSize: 5);
        _store.GetProductsPage(
                Arg.Any<Guid>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<AnalyticsProductTypeFilter>(),
                2,
                5,
                Arg.Any<AnalyticsProductSort>(),
                Arg.Any<AnalyticsSortDirection>(),
                Arg.Any<CancellationToken>())
            .Returns((Array.Empty<AnalyticsProductRow>(), 0));

        await _handler.Handle(new GetSellerAnalyticsProductsQuery(Guid.NewGuid(), req), CancellationToken.None);

        await _store.Received(1).GetProductsPage(
            Arg.Any<Guid>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<AnalyticsProductTypeFilter>(),
            2,
            5,
            Arg.Any<AnalyticsProductSort>(),
            Arg.Any<AnalyticsSortDirection>(),
            Arg.Any<CancellationToken>());
    }
}
