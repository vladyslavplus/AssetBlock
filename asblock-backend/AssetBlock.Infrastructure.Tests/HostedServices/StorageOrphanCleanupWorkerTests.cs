using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.Storage;
using AssetBlock.Infrastructure.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using System.Diagnostics.Metrics;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

[Collection(Observability.AssetBlockDiagnosticsCollection.NAME)]
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

    [Fact]
    public async Task RunCleanup_WhenListFails_ShouldRecordFailure()
    {
        var storage = Substitute.For<IAssetStorageService>();
        var assetStore = Substitute.For<IAssetStore>();

        storage.ListObjects("assets/", Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("storage offline"));

        var services = new ServiceCollection();
        services.AddScoped(_ => storage);
        services.AddScoped(_ => assetStore);
        await using var provider = services.BuildServiceProvider();

        var sut = new StorageOrphanCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IHostEnvironment>(),
            NullLogger<StorageOrphanCleanupWorker>.Instance);

        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == "AssetBlock.Backend")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };

        var recordedOutcomes = new List<string?>();
        var recordedFailures = new List<long>();

        listener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
        {
            if (instrument.Name == "assetblock.storage.orphan_cleanup.duration")
            {
                recordedOutcomes.Add(tags.ToArray().FirstOrDefault(t => t.Key == "cleanup.outcome").Value?.ToString());
            }
        });

        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
        {
            if (instrument.Name == "assetblock.storage.orphan_cleanup.failures")
            {
                recordedFailures.Add(measurement);
            }
        });

        listener.Start();

        var act = () => sut.RunCleanup(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        listener.RecordObservableInstruments();

        recordedOutcomes.Should().ContainSingle().Which.Should().Be("failure");
        recordedFailures.Should().ContainSingle().Which.Should().Be(1L);
    }
}
