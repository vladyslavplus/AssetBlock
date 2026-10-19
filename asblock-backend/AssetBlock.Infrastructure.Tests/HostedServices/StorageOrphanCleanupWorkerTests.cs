using System.Diagnostics.Metrics;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Primitives.Storage;
using AssetBlock.Infrastructure.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

[Collection(Observability.AssetBlockDiagnosticsCollection.NAME)]
public sealed class StorageOrphanCleanupWorkerTests
{
    [Fact]
    public async Task RunCleanup_WhenOnlyAnOldObjectHasNoAssetRow_ShouldDeleteOnlyThatObject()
    {
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        IStorageOrphanCleanupStateStore stateStore = CreateStateStore();
        DateTimeOffset old = DateTimeOffset.UtcNow - TimeSpan.FromHours(25);

        storage.ListObjectVersionsPage("assets/", Arg.Any<StorageObjectCursor?>(), 250, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StorageObjectPage(
            [
                new StorageObjectInfo("assets/orphan.zip", old, 10, "generation-1"),
                new StorageObjectInfo("assets/soft-deleted.zip", old, 10, "generation-2"),
                new StorageObjectInfo("assets/recent.zip", DateTimeOffset.UtcNow - TimeSpan.FromHours(1), 10)
            ], null, true)));
        assetStore.ExistsByStorageKey("assets/orphan.zip", Arg.Any<CancellationToken>()).Returns(false);
        // ExistsByStorageKey intentionally includes soft-deleted assets, whose blobs must remain available.
        assetStore.ExistsByStorageKey("assets/soft-deleted.zip", Arg.Any<CancellationToken>()).Returns(true);

        var services = new ServiceCollection();
        services.AddScoped(_ => storage);
        services.AddScoped(_ => assetStore);
        services.AddScoped(_ => stateStore);
        await using ServiceProvider provider = services.BuildServiceProvider();

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        var sut = new StorageOrphanCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<StorageOrphanCleanupWorker>.Instance);

        await sut.RunCleanup(CancellationToken.None);

        await storage.Received(1).Delete("assets/orphan.zip", "generation-1", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().Delete("assets/soft-deleted.zip", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await storage.DidNotReceive().Delete("assets/recent.zip", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_WhenListFails_ShouldRecordFailure()
    {
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        IStorageOrphanCleanupStateStore stateStore = CreateStateStore();

        storage.ListObjectVersionsPage("assets/", Arg.Any<StorageObjectCursor?>(), 250, Arg.Any<CancellationToken>())
            .Returns<Task<StorageObjectPage>>(_ => throw new InvalidOperationException("storage offline"));

        var services = new ServiceCollection();
        services.AddScoped(_ => storage);
        services.AddScoped(_ => assetStore);
        services.AddScoped(_ => stateStore);
        await using ServiceProvider provider = services.BuildServiceProvider();

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

        Func<Task> act = () => sut.RunCleanup(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        listener.RecordObservableInstruments();

        recordedOutcomes.Should().ContainSingle().Which.Should().Be("failure");
        recordedFailures.Should().ContainSingle().Which.Should().Be(1L);
    }

    [Fact]
    public async Task RunCleanup_UsesDeterministicTimeProviderForOrphanCutoff()
    {
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        IStorageOrphanCleanupStateStore stateStore = CreateStateStore();
        var frozenNow = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new ControllableTimeProvider(frozenNow);

        DateTimeOffset olderThanCutoff = frozenNow - TimeSpan.FromHours(25);
        DateTimeOffset newerThanCutoff = frozenNow - TimeSpan.FromHours(23);

        storage.ListObjectVersionsPage("assets/", Arg.Any<StorageObjectCursor?>(), 250, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StorageObjectPage(
            [
                new StorageObjectInfo("assets/orphan-old.zip", olderThanCutoff, 10, "generation-old"),
                new StorageObjectInfo("assets/orphan-recent.zip", newerThanCutoff, 10)
            ], null, true)));
        assetStore.ExistsByStorageKey(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var services = new ServiceCollection();
        services.AddScoped(_ => storage);
        services.AddScoped(_ => assetStore);
        services.AddScoped(_ => stateStore);
        await using ServiceProvider provider = services.BuildServiceProvider();

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        var sut = new StorageOrphanCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            environment,
            NullLogger<StorageOrphanCleanupWorker>.Instance,
            timeProvider);

        await sut.RunCleanup(CancellationToken.None);

        await storage.Received(1).Delete("assets/orphan-old.zip", "generation-old", Arg.Any<CancellationToken>());
        await storage.DidNotReceive().Delete("assets/orphan-recent.zip", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunCleanup_WhenOldUnreferencedObjectHasNoGeneration_ShouldRetainIt()
    {
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        IStorageOrphanCleanupStateStore stateStore = CreateStateStore();
        storage.ListObjectVersionsPage("assets/", Arg.Any<StorageObjectCursor?>(), 250, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StorageObjectPage(
                [new StorageObjectInfo("assets/unknown.zip", DateTimeOffset.UtcNow.AddDays(-2), 10)], null, true)));
        assetStore.ExistsByStorageKey("assets/unknown.zip", Arg.Any<CancellationToken>()).Returns(false);

        ServiceCollection services = new();
        services.AddScoped(_ => storage);
        services.AddScoped(_ => assetStore);
        services.AddScoped(_ => stateStore);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        StorageOrphanCleanupWorker sut = new(provider.GetRequiredService<IServiceScopeFactory>(), environment,
            NullLogger<StorageOrphanCleanupWorker>.Instance);

        await sut.RunCleanup(CancellationToken.None);

        await storage.DidNotReceiveWithAnyArgs().Delete(null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task RunCleanup_WhenReferenceAppearsAtFinalProtectionCheck_ShouldNotDelete()
    {
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        IStorageOrphanCleanupStateStore stateStore = CreateStateStore();
        storage.ListObjectVersionsPage("assets/", Arg.Any<StorageObjectCursor?>(), 250, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StorageObjectPage([new StorageObjectInfo(
                "assets/protected-at-recheck.zip", DateTimeOffset.UtcNow.AddDays(-2), 10, "generation-1")], null, true)));
        assetStore.ExistsByStorageKey("assets/protected-at-recheck.zip", Arg.Any<CancellationToken>())
            .Returns(false, true);

        ServiceCollection services = new();
        services.AddScoped(_ => storage);
        services.AddScoped(_ => assetStore);
        services.AddScoped(_ => stateStore);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        var sut = new StorageOrphanCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), environment,
            NullLogger<StorageOrphanCleanupWorker>.Instance);

        await sut.RunCleanup(CancellationToken.None);

        await assetStore.Received(2).ExistsByStorageKey("assets/protected-at-recheck.zip", Arg.Any<CancellationToken>());
        await storage.DidNotReceiveWithAnyArgs().Delete(null!, null!, CancellationToken.None);
    }

    [Fact]
    public async Task RunCleanup_WhenScanLeaseIsLost_ShouldStopBeforeDelete()
    {
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        IStorageOrphanCleanupStateStore stateStore = CreateStateStore();
        storage.ListObjectVersionsPage("assets/", Arg.Any<StorageObjectCursor?>(), 250, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new StorageObjectPage([new StorageObjectInfo(
                "assets/lease-lost.zip", DateTimeOffset.UtcNow.AddDays(-2), 10, "generation-1")], null, true)));
        stateStore.SaveCursor(Arg.Any<Guid>(), Arg.Any<StorageObjectCursor?>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(false);

        ServiceCollection services = new();
        services.AddScoped(_ => storage);
        services.AddScoped(_ => assetStore);
        services.AddScoped(_ => stateStore);
        await using ServiceProvider provider = services.BuildServiceProvider();
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        var sut = new StorageOrphanCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), environment,
            NullLogger<StorageOrphanCleanupWorker>.Instance);

        await sut.RunCleanup(CancellationToken.None);

        await assetStore.DidNotReceiveWithAnyArgs().ExistsByStorageKey(null!, CancellationToken.None);
        await storage.DidNotReceiveWithAnyArgs().Delete(null!, null!, CancellationToken.None);
    }

    private sealed class ControllableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static IStorageOrphanCleanupStateStore CreateStateStore()
    {
        IStorageOrphanCleanupStateStore stateStore = Substitute.For<IStorageOrphanCleanupStateStore>();
        stateStore.TryAcquire(Arg.Any<DateTimeOffset>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<StorageOrphanCleanupLease?>(new StorageOrphanCleanupLease(Guid.NewGuid(), null)));
        stateStore.SaveCursor(Arg.Any<Guid>(), Arg.Any<StorageObjectCursor?>(), Arg.Any<DateTimeOffset>(),
                Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(true);
        stateStore.Release(Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        return stateStore;
    }

    private static async IAsyncEnumerable<T> ToAsyncEnumerable<T>(IEnumerable<T> items)
    {
        foreach (T item in items)
        {
            yield return item;
        }
        await Task.CompletedTask;
    }

    private static async IAsyncEnumerable<T> ThrowingAsyncEnumerable<T>(Exception ex)
    {
        await Task.Yield();
        if (ex is not null)
        {
            throw ex;
        }
        yield break;
    }
}
