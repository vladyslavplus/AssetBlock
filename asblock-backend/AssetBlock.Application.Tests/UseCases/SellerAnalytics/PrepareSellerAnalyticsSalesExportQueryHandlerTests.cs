using Ardalis.Result;
using AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.SellerAnalytics;

public sealed class PrepareSellerAnalyticsSalesExportQueryHandlerTests
{
    private readonly ISellerAnalyticsStore _store = Substitute.For<ISellerAnalyticsStore>();
    private readonly PrepareSellerAnalyticsSalesExportQueryHandler _handler;

    public PrepareSellerAnalyticsSalesExportQueryHandlerTests()
    {
        _handler = new PrepareSellerAnalyticsSalesExportQueryHandler(_store);
    }

    [Fact]
    public async Task Handle_WhenCapExceeded_ShouldReturnExportTooLargeAndDisposeSession()
    {
        var session = new ExceedsMaxExportSessionStub();
        _store.OpenSalesExportSession(
                Arg.Any<Guid>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<AnalyticsProductTypeFilter>(),
                Arg.Any<CancellationToken>())
            .Returns(session);

        Result<PreparedSellerAnalyticsSalesExport> result = await _handler.Handle(
            new PrepareSellerAnalyticsSalesExportQuery(
                Guid.NewGuid(),
                new DateOnly(2024, 1, 1),
                new DateOnly(2024, 2, 1),
                AnalyticsProductTypeFilter.ALL),
            CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().ContainSingle(e => e.Identifier == ErrorCodes.ERR_ANALYTICS_EXPORT_TOO_LARGE);
        session.Disposed.Should().BeTrue();
    }

    [Fact]
    public async Task Handle_WhenWithinCap_ShouldReturnPreparedSession()
    {
        var session = new WithinCapExportSessionStub();
        _store.OpenSalesExportSession(
                Arg.Any<Guid>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<AnalyticsProductTypeFilter>(),
                Arg.Any<CancellationToken>())
            .Returns(session);

        Result<PreparedSellerAnalyticsSalesExport> result = await _handler.Handle(
            new PrepareSellerAnalyticsSalesExportQuery(
                Guid.NewGuid(),
                new DateOnly(2024, 1, 1),
                new DateOnly(2024, 2, 1),
                AnalyticsProductTypeFilter.ALL),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Session.Should().BeSameAs(session);
        session.Disposed.Should().BeFalse();
    }

    private sealed class ExceedsMaxExportSessionStub : ISellerAnalyticsSalesExportSession
    {
        public bool Disposed { get; private set; }

        public bool ExceedsMax => true;

        public IAsyncEnumerable<AnalyticsSalesExportRow> ReadRows(CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<AnalyticsSalesExportRow>();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class WithinCapExportSessionStub : ISellerAnalyticsSalesExportSession
    {
        public bool Disposed { get; private set; }

        public bool ExceedsMax => false;

        public IAsyncEnumerable<AnalyticsSalesExportRow> ReadRows(CancellationToken cancellationToken = default) =>
            AsyncEnumerable.Empty<AnalyticsSalesExportRow>();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
