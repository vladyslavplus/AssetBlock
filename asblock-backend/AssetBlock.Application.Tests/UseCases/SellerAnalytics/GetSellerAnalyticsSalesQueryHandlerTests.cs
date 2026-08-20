using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsSales;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.SellerAnalytics;

public sealed class GetSellerAnalyticsSalesQueryHandlerTests
{
    private readonly ISellerAnalyticsStore _store = Substitute.For<ISellerAnalyticsStore>();
    private readonly ITypedCache _cache = Substitute.For<ITypedCache>();
    private readonly GetSellerAnalyticsSalesQueryHandler _handler;

    public GetSellerAnalyticsSalesQueryHandlerTests()
    {
        _handler = new GetSellerAnalyticsSalesQueryHandler(
            _store,
            _cache,
            NullLogger<GetSellerAnalyticsSalesQueryHandler>.Instance);

        _cache.Get<AnalyticsSalesResult>(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AnalyticsSalesResult?)null);
    }

    [Fact]
    public async Task Handle_WhenStoreReturnsRows_IncludesPeriodMetadata()
    {
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        _store.GetSalesPage(
                Arg.Any<Guid>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<AnalyticsProductTypeFilter>(),
                null,
                null,
                25,
                Arg.Any<CancellationToken>())
            .Returns((
                new List<AnalyticsSaleRow>
                {
                    new(
                        AnalyticsProductKind.ASSET,
                        productId,
                        "Asset",
                        orderId,
                        DateTimeOffset.UtcNow,
                        1,
                        9.99m)
                },
                false));

        var req = new AnalyticsSalesRequest(new DateOnly(2024, 1, 1), new DateOnly(2024, 2, 1));
        var result = await _handler.Handle(new GetSellerAnalyticsSalesQuery(Guid.NewGuid(), req), CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Items.Should().HaveCount(1);
        result.Value.Items[0].GrossRevenueCents.Should().Be(999);
        result.Value.From.Should().Be(req.From);
        result.Value.Currency.Should().Be(AnalyticsConstants.CURRENCY);
    }
}
