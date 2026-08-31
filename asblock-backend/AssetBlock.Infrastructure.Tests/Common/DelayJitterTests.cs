using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Common;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.Common;

public sealed class DelayJitterTests
{
    [Fact]
    public void Apply_WhenZeroOrNegative_ShouldReturnZero()
    {
        DelayJitter.Apply(TimeSpan.Zero, () => 0.5).Should().Be(TimeSpan.Zero);
        DelayJitter.Apply(TimeSpan.FromSeconds(-5), () => 0.5).Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Apply_WhenDeterministicFactor_ShouldScaleAccurately()
    {
        var baseDelay = TimeSpan.FromSeconds(100);

        // factor = 0.0 -> multiplier = 0.80 (-20%)
        DelayJitter.Apply(baseDelay, () => 0.0).Should().Be(TimeSpan.FromSeconds(80));

        // factor = 0.5 -> multiplier = 1.00 (exact base)
        DelayJitter.Apply(baseDelay, () => 0.5).Should().Be(TimeSpan.FromSeconds(100));

        // factor = 1.0 -> multiplier = 1.20 (+20%)
        DelayJitter.Apply(baseDelay, () => 1.0).Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public void Apply_WhenRandom_ShouldStayWithinBounds()
    {
        var baseDelay = TimeSpan.FromSeconds(10);
        for (var i = 0; i < 100; i++)
        {
            TimeSpan jittered = DelayJitter.Apply(baseDelay);
            jittered.Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(8));
            jittered.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(12));
        }
    }

    [Fact]
    public void AssetProcessingWorker_CalculateRetryDelay_WithJitter_ShouldRespectBoundsAndCaps()
    {
        var options = new AssetProcessingOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(30),
            MaxRetryDelay = TimeSpan.FromHours(1)
        };

        AssetProcessingWorker workerMin = CreateWorker(options, () => 0.0); // 0.8x multiplier
        AssetProcessingWorker workerMid = CreateWorker(options, () => 0.5); // 1.0x multiplier
        AssetProcessingWorker workerMax = CreateWorker(options, () => 1.0); // 1.2x multiplier

        // Attempt 1: base exponential delay is 30s * 2^0 = 30s
        workerMin.CalculateRetryDelay(1, null).Should().Be(TimeSpan.FromSeconds(24));
        workerMid.CalculateRetryDelay(1, null).Should().Be(TimeSpan.FromSeconds(30));
        workerMax.CalculateRetryDelay(1, null).Should().Be(TimeSpan.FromSeconds(36));

        // Handler RetryAfter is treated as a lower bound
        var retryAfter = TimeSpan.FromSeconds(50);
        workerMin.CalculateRetryDelay(1, retryAfter).Should().Be(TimeSpan.FromSeconds(50));
        workerMax.CalculateRetryDelay(1, retryAfter).Should().Be(TimeSpan.FromSeconds(50));

        // MaxRetryDelay is respected as a ceiling
        workerMax.CalculateRetryDelay(50, null).Should().Be(TimeSpan.FromHours(1));

        // Overflow safety for huge attempt counts
        TimeSpan hugeAttemptDelay = workerMid.CalculateRetryDelay(1000, null);
        hugeAttemptDelay.Should().Be(TimeSpan.FromHours(1));
    }

    private static AssetProcessingWorker CreateWorker(
        AssetProcessingOptions options,
        Func<double> jitterProvider)
    {
        IServiceScopeFactory scopeFactory = Substitute.For<IServiceScopeFactory>();
        IAssetProcessingJobRegistry registry = Substitute.For<IAssetProcessingJobRegistry>();
        IAssetProcessingRealtimePublisher publisher = Substitute.For<IAssetProcessingRealtimePublisher>();
        IOptions<AssetProcessingOptions> optionsWrapper = Microsoft.Extensions.Options.Options.Create(options);

        return new AssetProcessingWorker(
            scopeFactory,
            registry,
            publisher,
            optionsWrapper,
            TimeProvider.System,
            NullLogger<AssetProcessingWorker>.Instance,
            jitterProvider);
    }
}
