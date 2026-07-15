using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.Storage;
using AssetBlock.Infrastructure.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class StorageOrphanCleanupWorkerTests
{
    [Fact]
    public async Task RunCleanup_WhenOnlyAnOldObjectHasNoAssetRow_ShouldDeleteOnlyThatObject()
    {
        var storage = Substitute.For<IAssetStorageService>();
        var assetStore = Substitute.For<IAssetStore>();
        var old = DateTimeOffset.UtcNow - TimeSpan.FromHours(25);

        storage.ListObjects("assets/", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<StorageObjectInfo>>(
            [
                new StorageObjectInfo("assets/orphan.zip", old, 10),
                new StorageObjectInfo("assets/soft-deleted.zip", old, 10),
                new StorageObjectInfo("assets/recent.zip", DateTimeOffset.UtcNow - TimeSpan.FromHours(1), 10)
            ]));
        assetStore.ExistsByStorageKey("assets/orphan.zip", Arg.Any<CancellationToken>()).Returns(false);
        // ExistsByStorageKey intentionally includes soft-deleted assets, whose blobs must remain available.
        assetStore.ExistsByStorageKey("assets/soft-deleted.zip", Arg.Any<CancellationToken>()).Returns(true);

        var services = new ServiceCollection();
        services.AddScoped(_ => storage);
        services.AddScoped(_ => assetStore);
        await using var provider = services.BuildServiceProvider();

        var environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        var sut = new StorageOrphanCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<StorageOrphanCleanupWorker>.Instance);

        await sut.RunCleanup(CancellationToken.None);

        await storage.Received(1).Delete("assets/orphan.zip", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().Delete("assets/soft-deleted.zip", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().Delete("assets/recent.zip", Arg.Any<CancellationToken>());
    }
}
