using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Infrastructure.Tests.Outbox;

[Collection(Observability.AssetBlockDiagnosticsCollection.NAME)]
public sealed class OutboxDispatcherTests
{
    [Fact]
    public async Task DispatchBatch_WhenDependencyFails_ShouldRecordRetryWithoutProcessingMessage()
    {
        var lockToken = Guid.NewGuid();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "test.external",
            Payload = "{}",
            LockToken = lockToken,
            AttemptCount = 1
        };
        var outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.MarkFailed(
                message.Id,
                lockToken,
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = Substitute.For<IOutboxMessageHandler>();
        handler.MessageType.Returns(message.Type);
        handler.Handle(message, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("dependency unavailable"));

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        services.AddSingleton(handler);
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance);
        var startedAt = DateTimeOffset.UtcNow;

        await dispatcher.DispatchBatch(CancellationToken.None);

        await outbox.Received(1).MarkFailed(
            message.Id,
            lockToken,
            "dependency unavailable",
            Arg.Is<DateTimeOffset>(next => next > startedAt),
            Arg.Any<CancellationToken>());
        await outbox.DidNotReceive().MarkProcessed(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchBatch_WhenHandlerMissing_ShouldRecordDeadLetterWithElapsedDuration()
    {
        var lockToken = Guid.NewGuid();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "test.missing",
            Payload = "{}",
            LockToken = lockToken
        };
        var outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.MarkDeadLettered(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                await Task.Delay(15);
                return true;
            });

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance);

        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "AssetBlock.Backend")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        var recordedOutcomes = new List<string?>();
        var recordedDurations = new List<double>();

        listener.SetMeasurementEventCallback<double>((instrument, measurement, tags, _) =>
        {
            if (instrument.Name == "assetblock.outbox.processing.duration")
            {
                recordedOutcomes.Add(tags.ToArray().FirstOrDefault(t => t.Key == "outbox.outcome").Value?.ToString());
                recordedDurations.Add(measurement);
            }
        });

        listener.Start();

        await dispatcher.DispatchBatch(CancellationToken.None);

        listener.RecordObservableInstruments();

        recordedOutcomes.Should().ContainSingle().Which.Should().Be("dead_letter");
        recordedDurations.Should().ContainSingle().Which.Should().BeGreaterThan(0);
        await outbox.Received(1).MarkDeadLettered(message.Id, lockToken, Arg.Is<string>(s => s.Contains("test.missing")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchBatch_WhenMaxAttemptsReached_ShouldTransitionToDeadLetter()
    {
        var lockToken = Guid.NewGuid();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "test.external",
            Payload = "{}",
            LockToken = lockToken,
            AttemptCount = 10
        };
        var outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.MarkDeadLettered(message.Id, lockToken, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var handler = Substitute.For<IOutboxMessageHandler>();
        handler.MessageType.Returns(message.Type);
        handler.Handle(message, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("persistent failure"));

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        services.AddSingleton(handler);
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance);

        await dispatcher.DispatchBatch(CancellationToken.None);

        await outbox.Received(1).MarkDeadLettered(
            message.Id,
            lockToken,
            "persistent failure",
            Arg.Any<CancellationToken>());
        await outbox.DidNotReceive().MarkFailed(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchBatch_WhenMissingHandlerMarkFails_ShouldRecordFailureOnce()
    {
        var lockToken = Guid.NewGuid();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "test.missing",
            Payload = "{}",
            LockToken = lockToken
        };
        var outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.MarkDeadLettered(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db offline"));

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        await using var provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance);

        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "AssetBlock.Backend")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        var recordedOutcomes = new List<string?>();

        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (instrument.Name == "assetblock.outbox.processing.duration")
            {
                recordedOutcomes.Add(tags.ToArray().FirstOrDefault(t => t.Key == "outbox.outcome").Value?.ToString());
            }
        });

        listener.Start();

        var act = () => dispatcher.DispatchBatch(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        listener.RecordObservableInstruments();

        recordedOutcomes.Should().ContainSingle().Which.Should().Be("failure");
    }
}
