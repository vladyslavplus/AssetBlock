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
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        storage.EnsureBucket(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        await using ServiceProvider provider = BuildProvider(storage);
        StorageBucketEnsureHostedService sut = CreateSut(provider);

        await sut.StartAsync(CancellationToken.None);
        await WaitForIdle(sut);
        await sut.StopAsync(CancellationToken.None);

        await storage.Received(1).EnsureBucket(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenEnsureBucketAlwaysFails_ShouldNotPropagateAndKeepRetryingUntilStopped()
    {
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        var calls = 0;
        var secondAttemptReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        storage.EnsureBucket(Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref calls) >= 2)
                {
                    secondAttemptReached.TrySetResult();
                }

                return Task.FromException(new InvalidOperationException("boom"));
            });

        await using ServiceProvider provider = BuildProvider(storage);
        StorageBucketEnsureHostedService sut = CreateSut(provider);

        await sut.StartAsync(CancellationToken.None);
        try
        {
            await secondAttemptReached.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await sut.StopAsync(CancellationToken.None);
        }

        calls.Should().BeGreaterThan(1);
        await storage.Received(calls).EnsureBucket(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_WhenStorageRecoversAfterFastAttempts_ShouldEventuallySucceed()
    {
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
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

        await using ServiceProvider provider = BuildProvider(storage);
        StorageBucketEnsureHostedService sut = CreateSut(provider);

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
        Task? execute = sut.ExecuteTask;
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
