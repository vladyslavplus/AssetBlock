using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Infrastructure.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class StorageBucketEnsureHostedServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenEnsureBucketSucceeds_ShouldStopAfterOneAttempt()
    {
        var storage = Substitute.For<IAssetStorageService>();
        storage.EnsureBucket(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await using var provider = BuildProvider(storage);
        var sut = CreateSut(provider);

        await sut.StartAsync(CancellationToken.None);
        await WaitForIdle(sut);
        await sut.StopAsync(CancellationToken.None);

        await storage.Received(1).EnsureBucket(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenEnsureBucketAlwaysFails_ShouldNotPropagateAndKeepRetryingUntilStopped()
    {
        var storage = Substitute.For<IAssetStorageService>();
        storage.EnsureBucket(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("boom")));

        await using var provider = BuildProvider(storage);
        using var cts = new CancellationTokenSource();
        var sut = CreateSut(provider);

        await sut.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);
        await cts.CancelAsync();
        await WaitForIdle(sut);

        var calls = storage.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IAssetStorageService.EnsureBucket));
        calls.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStorageRecoversAfterFastAttempts_ShouldEventuallySucceed()
    {
        var storage = Substitute.For<IAssetStorageService>();
        var calls = 0;
        storage.EnsureBucket(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                calls++;
                if (calls <= 3)
                {
                    return Task.FromException(new InvalidOperationException($"down-{calls}"));
                }

                return Task.CompletedTask;
            });

        await using var provider = BuildProvider(storage);
        var sut = CreateSut(provider);

        await sut.StartAsync(CancellationToken.None);
        await WaitForIdle(sut, timeout: TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        calls.Should().BeGreaterThanOrEqualTo(4);
        await storage.Received(calls).EnsureBucket(Arg.Any<CancellationToken>());
    }

    private static ServiceProvider BuildProvider(IAssetStorageService storage)
    {
        var services = new ServiceCollection();
        services.AddSingleton(storage);
        return services.BuildServiceProvider();
    }

    private static StorageBucketEnsureHostedService CreateSut(IServiceProvider provider) =>
        new(
            provider,
            NullLogger<StorageBucketEnsureHostedService>.Instance,
            fastRetryDelay: TimeSpan.FromMilliseconds(10),
            initialBackoff: TimeSpan.FromMilliseconds(20),
            maxBackoff: TimeSpan.FromMilliseconds(40));

    private static async Task WaitForIdle(BackgroundService sut, TimeSpan? timeout = null)
    {
        var execute = sut.ExecuteTask;
        if (execute is null)
        {
            return;
        }

        using var timeoutCts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(2));
        try
        {
            await execute.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            // Still retrying (expected for always-fail case before StopAsync).
        }
    }
}
