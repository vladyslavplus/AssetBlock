using AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Application.Tests.UseCases.SellerAnalytics;

public sealed class ExportSellerAnalyticsSalesCommandHandlerTests
{
    private readonly IAuditWriter _auditWriter = Substitute.For<IAuditWriter>();
    private readonly ExportSellerAnalyticsSalesCommandHandler _handler;

    public ExportSellerAnalyticsSalesCommandHandlerTests()
    {
        _handler = new ExportSellerAnalyticsSalesCommandHandler(
            _auditWriter,
            NullLogger<ExportSellerAnalyticsSalesCommandHandler>.Instance);
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldStreamCsvAndAuditOnce()
    {
        var sellerId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var productId = Guid.NewGuid();
        var rows = new[]
        {
            new AnalyticsSalesExportRow(
                new DateTimeOffset(2024, 5, 1, 10, 0, 0, TimeSpan.Zero),
                orderId,
                "ASSET",
                productId,
                "Title",
                1,
                12.34m)
        };

        await using var session = new FakeSalesExportSession(rows);

        await using var output = new MemoryStream();
        var command = new ExportSellerAnalyticsSalesCommand(
            sellerId,
            new DateOnly(2024, 5, 1),
            new DateOnly(2024, 5, 2),
            AnalyticsProductTypeFilter.ALL,
            output,
            session);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);

        var text = System.Text.Encoding.UTF8.GetString(output.ToArray());
        text.Should().StartWith("\uFEFF");
        text.Should().Contain(SellerAnalyticsSalesCsvFormatter.HEADER);
        text.Should().Contain("ASSET");
        text.Should().Contain("12.34");
        text.Should().Contain("\r\n");

        await _auditWriter.Received(1).WriteBestEffort(
            Arg.Is<Domain.Core.Dto.Audit.AuditEvent>(e =>
                e.Action == AuditActions.SELLER_ANALYTICS_EXPORTED
                && e.ResourceType == AuditResourceTypes.SELLER_ANALYTICS
                && e.Metadata!["rowCount"]!.Equals(1)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenSuccessful_ShouldDisposeSessionBeforeFlush()
    {
        var sellerId = Guid.NewGuid();
        var rows = new[]
        {
            new AnalyticsSalesExportRow(
                new DateTimeOffset(2024, 5, 1, 10, 0, 0, TimeSpan.Zero),
                Guid.NewGuid(),
                "ASSET",
                Guid.NewGuid(),
                "Title",
                1,
                12.34m)
        };

        var session = new FakeSalesExportSession(rows);
        await using var output = new TrackingMemoryStream();
        var command = new ExportSellerAnalyticsSalesCommand(
            sellerId,
            new DateOnly(2024, 5, 1),
            new DateOnly(2024, 5, 2),
            AnalyticsProductTypeFilter.ALL,
            output,
            session);

        await _handler.Handle(command, CancellationToken.None);

        session.Disposed.Should().BeTrue();
        session.DisposedAtSequence.Should().NotBeNull();
        output.FlushAsyncSequence.Should().NotBeNull();
        session.DisposedAtSequence.Should().BeLessThan(output.FlushAsyncSequence!.Value);
    }

    [Fact]
    public async Task Handle_WhenCapExceeded_ShouldReturnExportTooLargeWithoutWriting()
    {
        await using var session = new FakeSalesExportSession([], exceedsMax: true);

        await using var output = new MemoryStream();
        var command = new ExportSellerAnalyticsSalesCommand(
            Guid.NewGuid(),
            new DateOnly(2024, 5, 1),
            new DateOnly(2024, 5, 2),
            AnalyticsProductTypeFilter.ALL,
            output,
            session);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeFalse();
        result.ValidationErrors.Should().ContainSingle(e => e.Identifier == ErrorCodes.ERR_ANALYTICS_EXPORT_TOO_LARGE);
        output.ToArray().Should().BeEmpty();

        await _auditWriter.DidNotReceive().WriteBestEffort(
            Arg.Any<Domain.Core.Dto.Audit.AuditEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenCancelled_ShouldNotAudit()
    {
        await using var session = new CancelledSalesExportSession();

        await using var output = new MemoryStream();
        var command = new ExportSellerAnalyticsSalesCommand(
            Guid.NewGuid(),
            new DateOnly(2024, 5, 1),
            new DateOnly(2024, 5, 2),
            AnalyticsProductTypeFilter.ALL,
            output,
            session);

        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var act = () => _handler.Handle(command, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();

        await _auditWriter.DidNotReceive().WriteBestEffort(
            Arg.Any<Domain.Core.Dto.Audit.AuditEvent>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_WhenAuditTimesOut_ShouldStillSucceedAfterCsvFlush()
    {
        _auditWriter
            .WriteBestEffort(Arg.Any<Domain.Core.Dto.Audit.AuditEvent>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                var token = callInfo.Arg<CancellationToken>();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });

        var handler = new ExportSellerAnalyticsSalesCommandHandler(
            _auditWriter,
            NullLogger<ExportSellerAnalyticsSalesCommandHandler>.Instance,
            TimeSpan.FromMilliseconds(20));

        var rows = new[]
        {
            new AnalyticsSalesExportRow(
                new DateTimeOffset(2024, 5, 1, 10, 0, 0, TimeSpan.Zero),
                Guid.NewGuid(),
                "ASSET",
                Guid.NewGuid(),
                "Title",
                1,
                12.34m)
        };

        await using var session = new FakeSalesExportSession(rows);
        await using var output = new MemoryStream();
        var command = new ExportSellerAnalyticsSalesCommand(
            Guid.NewGuid(),
            new DateOnly(2024, 5, 1),
            new DateOnly(2024, 5, 2),
            AnalyticsProductTypeFilter.ALL,
            output,
            session);

        var result = await handler.Handle(command, CancellationToken.None);

        result.IsSuccess.Should().BeTrue();
        result.Value.Should().Be(1);
        output.Length.Should().BeGreaterThan(0);
    }

    private static int _sequence;

    private static int NextSequence() => Interlocked.Increment(ref _sequence);

    private sealed class FakeSalesExportSession(IEnumerable<AnalyticsSalesExportRow> rows, bool exceedsMax = false)
        : ISellerAnalyticsSalesExportSession
    {
        private readonly IReadOnlyList<AnalyticsSalesExportRow> _rows = rows.ToList();

        public bool Disposed { get; private set; }

        public int? DisposedAtSequence { get; private set; }

        public bool ExceedsMax { get; } = exceedsMax;

        public async IAsyncEnumerable<AnalyticsSalesExportRow> ReadRows(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var row in _rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return row;
                await Task.Yield();
            }
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            DisposedAtSequence = NextSequence();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class TrackingMemoryStream : MemoryStream
    {
        public int? FlushAsyncSequence { get; private set; }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            FlushAsyncSequence = NextSequence();
            return base.FlushAsync(cancellationToken);
        }
    }

    private sealed class CancelledSalesExportSession : ISellerAnalyticsSalesExportSession
    {
        public bool ExceedsMax => false;

        public async IAsyncEnumerable<AnalyticsSalesExportRow> ReadRows(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield break;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
