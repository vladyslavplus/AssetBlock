using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Infrastructure.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class MinioBucketEnsureHostedServiceTests
{
    [Fact]
    public async Task StartAsync_callsEnsureBucket_onStorage()
    {
        var storage = Substitute.For<IAssetStorageService>();
        storage.EnsureBucket(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var services = new ServiceCollection();
        services.AddSingleton(storage);
        var provider = services.BuildServiceProvider();

        var sut = new MinioBucketEnsureHostedService(provider, NullLogger<MinioBucketEnsureHostedService>.Instance);
        await sut.StartAsync(CancellationToken.None);

        await storage.Received(1).EnsureBucket(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task StartAsync_whenEnsureBucketThrows_doesNotPropagate()
    {
        var storage = Substitute.For<IAssetStorageService>();
        storage.EnsureBucket(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new InvalidOperationException("boom")));

        var services = new ServiceCollection();
        services.AddSingleton(storage);
        var provider = services.BuildServiceProvider();

        var sut = new MinioBucketEnsureHostedService(provider, NullLogger<MinioBucketEnsureHostedService>.Instance);
        var act = async () => await sut.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }
}
