using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Infrastructure.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class OutboxRetentionWorkerTests
{
    [Fact]
    public void CalculateInitialDelay_WithJitter_ShouldScaleAccurately()
    {
        // Initial delay base is 2 minutes (120s)
        OutboxRetentionWorker.CalculateInitialDelay(() => 0.0).Should().Be(TimeSpan.FromSeconds(96));
        OutboxRetentionWorker.CalculateInitialDelay(() => 0.5).Should().Be(TimeSpan.FromSeconds(120));
        OutboxRetentionWorker.CalculateInitialDelay(() => 1.0).Should().Be(TimeSpan.FromSeconds(144));
    }

    [Fact]
    public void CalculateIntervalDelay_WithJitter_ShouldScaleAccurately()
    {
        // Interval base is 1 hour (3600s)
        OutboxRetentionWorker.CalculateIntervalDelay(() => 0.0).Should().Be(TimeSpan.FromMinutes(48));
        OutboxRetentionWorker.CalculateIntervalDelay(() => 0.5).Should().Be(TimeSpan.FromMinutes(60));
        OutboxRetentionWorker.CalculateIntervalDelay(() => 1.0).Should().Be(TimeSpan.FromMinutes(72));
    }

    [Fact]
    public async Task RunCleanup_WhenProcessedOlderThanCutoff_ShouldCallCleanupProcessedInBatches()
    {
        DateTimeOffset now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset expectedCutoff = now.AddDays(-7);
        FakeTimeProvider timeProvider = new(now);

        IOutboxStore outboxStore = Substitute.For<IOutboxStore>();
        outboxStore.CleanupProcessed(expectedCutoff, 500, Arg.Any<CancellationToken>())
            .Returns(500, 150);

        ServiceCollection services = new();
        services.AddScoped(_ => outboxStore);
        ServiceProvider provider = services.BuildServiceProvider();

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);

        OutboxRetentionWorker sut = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<OutboxRetentionWorker>.Instance,
            timeProvider);

        await using (provider)
        {
            var deleted = await sut.RunCleanup(CancellationToken.None);
            deleted.Should().Be(650);
        }

        await outboxStore.Received(2).CleanupProcessed(expectedCutoff, 500, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_WhenNoOldProcessed_ShouldCallOnceAndReturnZero()
    {
        DateTimeOffset now = new(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        DateTimeOffset expectedCutoff = now.AddDays(-7);
        FakeTimeProvider timeProvider = new(now);

        IOutboxStore outboxStore = Substitute.For<IOutboxStore>();
        outboxStore.CleanupProcessed(expectedCutoff, 500, Arg.Any<CancellationToken>())
            .Returns(0);

        ServiceCollection services = new();
        services.AddScoped(_ => outboxStore);
        ServiceProvider provider = services.BuildServiceProvider();

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);

        OutboxRetentionWorker sut = new(
            provider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<OutboxRetentionWorker>.Instance,
            timeProvider);

        await using (provider)
        {
            var deleted = await sut.RunCleanup(CancellationToken.None);
            deleted.Should().Be(0);
        }

        await outboxStore.Received(1).CleanupProcessed(expectedCutoff, 500, Arg.Any<CancellationToken>());
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _utcNow = start;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
