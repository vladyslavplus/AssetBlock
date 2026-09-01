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
    public async Task DispatchBatch_WhenMultipleMessages_ShouldUseBoundedConcurrency()
    {
        var lockToken = Guid.NewGuid();
        OutboxMessage[] messages = Enumerable.Range(0, 6)
            .Select(index => new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "test.concurrent",
                Payload = "{}",
                LockToken = lockToken,
                AttemptCount = 1,
                OccurredAt = DateTimeOffset.UtcNow.AddMilliseconds(index)
            })
            .ToArray();
        IOutboxStore outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(messages);
        outbox.MarkProcessed(Arg.Any<Guid>(), lockToken, Arg.Any<CancellationToken>()).Returns(true);

        var active = 0;
        var maxActive = 0;
        IOutboxMessageHandler handler = Substitute.For<IOutboxMessageHandler>();
        handler.MessageType.Returns("test.concurrent");
        handler.Handle(Arg.Any<OutboxMessage>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var current = Interlocked.Increment(ref active);
            UpdateMaximum(ref maxActive, current);
            await Task.Delay(40, call.Arg<CancellationToken>());
            Interlocked.Decrement(ref active);
        });

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        services.AddSingleton(handler);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance,
            maxConcurrency: 2);

        await dispatcher.DispatchBatch(CancellationToken.None);

        maxActive.Should().Be(2);
        await outbox.Received(messages.Length).MarkProcessed(
            Arg.Any<Guid>(),
            lockToken,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchBatch_WhenHandlerOutlivesRenewalInterval_ShouldRenewLease()
    {
        var lockToken = Guid.NewGuid();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "test.slow",
            Payload = "{}",
            LockToken = lockToken,
            AttemptCount = 1
        };
        IOutboxStore outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.RenewLease(message.Id, lockToken, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        outbox.MarkProcessed(message.Id, lockToken, Arg.Any<CancellationToken>()).Returns(true);

        IOutboxMessageHandler handler = Substitute.For<IOutboxMessageHandler>();
        handler.MessageType.Returns(message.Type);
        handler.Handle(message, Arg.Any<CancellationToken>()).Returns(async call =>
            await Task.Delay(100, call.Arg<CancellationToken>()));

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        services.AddSingleton(handler);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance,
            leaseDuration: TimeSpan.FromMilliseconds(60));

        await dispatcher.DispatchBatch(CancellationToken.None);

        await outbox.Received().RenewLease(
            message.Id,
            lockToken,
            TimeSpan.FromMilliseconds(60),
            Arg.Any<CancellationToken>());
        await outbox.Received(1).MarkProcessed(message.Id, lockToken, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DispatchBatch_WhenLeaseRenewalLosesOwnership_ShouldCancelHandlerWithoutMarkingMessage()
    {
        var lockToken = Guid.NewGuid();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "test.lease-lost",
            Payload = "{}",
            LockToken = lockToken,
            AttemptCount = 1
        };
        IOutboxStore outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.RenewLease(message.Id, lockToken, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false);

        IOutboxMessageHandler handler = Substitute.For<IOutboxMessageHandler>();
        handler.MessageType.Returns(message.Type);
        handler.Handle(message, Arg.Any<CancellationToken>()).Returns(async call =>
            await Task.Delay(Timeout.InfiniteTimeSpan, call.Arg<CancellationToken>()));

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        services.AddSingleton(handler);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance,
            leaseDuration: TimeSpan.FromMilliseconds(30));

        await dispatcher.DispatchBatch(CancellationToken.None);

        await outbox.Received(1).RenewLease(
            message.Id,
            lockToken,
            TimeSpan.FromMilliseconds(30),
            Arg.Any<CancellationToken>());
        await outbox.DidNotReceive().MarkProcessed(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<CancellationToken>());
        await outbox.DidNotReceive().MarkFailed(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<CancellationToken>());
        await outbox.DidNotReceive().MarkDeadLettered(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int observed;
        do
        {
            observed = Volatile.Read(ref target);
            if (candidate <= observed)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, observed) != observed);
    }

    [Fact]
    public void CalculatePollInterval_WithJitter_ShouldScaleAccurately()
    {
        // PollInterval base is 2.0s
        OutboxDispatcher.CalculatePollInterval(() => 0.0).Should().Be(TimeSpan.FromSeconds(1.6));
        OutboxDispatcher.CalculatePollInterval(() => 0.5).Should().Be(TimeSpan.FromSeconds(2.0));
        OutboxDispatcher.CalculatePollInterval(() => 1.0).Should().Be(TimeSpan.FromSeconds(2.4));
    }

    [Theory]
    [InlineData(1, 0.0, 1.6)]     // 2s * 0.8 = 1.6s
    [InlineData(1, 0.5, 2.0)]     // 2s * 1.0 = 2.0s
    [InlineData(1, 1.0, 2.4)]     // 2s * 1.2 = 2.4s
    [InlineData(10, 1.0, 1228.8)] // 1024s * 1.2 = 1228.8s
    [InlineData(15, 1.0, 1228.8)] // Exponent capped at 10 (1024s * 1.2 = 1228.8s)
    public void CalculateRetryDelay_WithJitter_ShouldScaleAndCap(int attempt, double factor, double expectedSeconds)
    {
        TimeSpan delay = OutboxDispatcher.CalculateRetryDelay(attempt, () => factor);
        delay.TotalSeconds.Should().BeApproximately(expectedSeconds, 0.01);
    }

    [Fact]
    public async Task DispatchBatch_WhenRetryScheduled_ShouldPassDeterministicJitterNextTime()
    {
        var lockToken = Guid.NewGuid();
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "test.jitter",
            Payload = "{}",
            LockToken = lockToken,
            AttemptCount = 1 // base 2s * 0.8 = 1.6s
        };
        IOutboxStore outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.MarkFailed(
                message.Id,
                lockToken,
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        IOutboxMessageHandler handler = Substitute.For<IOutboxMessageHandler>();
        handler.MessageType.Returns(message.Type);
        handler.Handle(message, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("transient error"));

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        services.AddSingleton(handler);
        await using ServiceProvider provider = services.BuildServiceProvider();

        // Pass deterministic jitter factor 0.0 -> 0.8x base delay = 1.6s
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance,
            () => 0.0);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        await dispatcher.DispatchBatch(CancellationToken.None);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        await outbox.Received(1).MarkFailed(
            message.Id,
            lockToken,
            "transient error",
            Arg.Is<DateTimeOffset>(next => next >= before.AddSeconds(1.5) && next <= after.AddSeconds(1.7)),
            Arg.Any<CancellationToken>());
    }

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
        IOutboxStore outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.MarkFailed(
                message.Id,
                lockToken,
                Arg.Any<string>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        IOutboxMessageHandler handler = Substitute.For<IOutboxMessageHandler>();
        handler.MessageType.Returns(message.Type);
        handler.Handle(message, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("dependency unavailable"));

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        services.AddSingleton(handler);
        await using ServiceProvider provider = services.BuildServiceProvider();
        var dispatcher = new OutboxDispatcher(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<OutboxDispatcher>.Instance);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

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
        IOutboxStore outbox = Substitute.For<IOutboxStore>();
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
        await using ServiceProvider provider = services.BuildServiceProvider();
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
        IOutboxStore outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.MarkDeadLettered(message.Id, lockToken, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(true);

        IOutboxMessageHandler handler = Substitute.For<IOutboxMessageHandler>();
        handler.MessageType.Returns(message.Type);
        handler.Handle(message, Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("persistent failure"));

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        services.AddSingleton(handler);
        await using ServiceProvider provider = services.BuildServiceProvider();
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
        IOutboxStore outbox = Substitute.For<IOutboxStore>();
        outbox.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns([message]);
        outbox.MarkDeadLettered(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("db offline"));

        var services = new ServiceCollection();
        services.AddSingleton(outbox);
        await using ServiceProvider provider = services.BuildServiceProvider();
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

        Func<Task> act = () => dispatcher.DispatchBatch(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        listener.RecordObservableInstruments();

        recordedOutcomes.Should().ContainSingle().Which.Should().Be("failure");
    }
}
