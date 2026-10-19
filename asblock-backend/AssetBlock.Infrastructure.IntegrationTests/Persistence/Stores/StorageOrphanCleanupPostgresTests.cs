using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.Storage;
using AssetBlock.Infrastructure.HostedServices;
using AssetBlock.Infrastructure.IntegrationTests.Storage;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class StorageOrphanCleanupPostgresTests(PostgresFixture fixture, MinioStorageFixture minioFixture)
    : IClassFixture<MinioStorageFixture>
{
    [Fact]
    public async Task RunCleanup_WithMoreThanOneThousandProviderVersions_ShouldFollowVersionMarkersAcrossRestarts()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var runId = Guid.NewGuid().ToString("N");
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: $"Version marker scan {runId}");
        db.Assets.Add(asset);
        var referenced = new List<(string Key, string Generation)>();
        for (var index = 0; index < 1001; index++)
        {
            var key = $"assets/{runId}/early/{index:D4}";
            var generation = await Upload(key);
            AssetVersion version = TestData.CreateAssetVersion(
                asset.Id, key, versionNumber: index + 1, isCurrent: index == 1000,
                processingStatus: AssetVersionProcessingStatus.READY);
            version.StorageVersionId = generation;
            db.AssetVersions.Add(version);
            referenced.Add((key, generation));
        }

        var sameKey = $"assets/{runId}/zzzz-same-key";
        var olderGeneration = await Upload(sameKey);
        var newerGeneration = await Upload(sameKey);
        AssetVersion sameKeyVersion = TestData.CreateAssetVersion(
            asset.Id, sameKey, versionNumber: 1002, isCurrent: false,
            processingStatus: AssetVersionProcessingStatus.READY);
        sameKeyVersion.StorageVersionId = newerGeneration;
        db.AssetVersions.Add(sameKeyVersion);
        var laterOrphanKey = $"assets/{runId}/zzzz-later-orphan";
        _ = await Upload(laterOrphanKey);
        await db.SaveChangesAsync();

        DateTimeOffset futureCutoff = DateTimeOffset.UtcNow.AddDays(2);
        (string Key, string Generation)? removedMarker = null;
        var observedCursors = new List<string?>();
        for (var sweep = 0; sweep < 12; sweep++)
        {
            await using ServiceProvider restartedProcess = CreateProvider(db);
            await CreateWorker(restartedProcess, futureCutoff).RunCleanup(CancellationToken.None);
            StorageOrphanCleanupState state = await db.StorageOrphanCleanupStates.AsNoTracking().SingleAsync();
            observedCursors.Add(state.CursorKey);

            if (sweep == 0)
            {
                state.CursorKey.Should().NotBeNull();
                state.CursorVersionId.Should().NotBeNullOrWhiteSpace();
                // Simulate provider lifecycle removal of the saved generation. S3 markers are
                // ordering positions; a missing version must not be mistaken for end-of-list.
                removedMarker = (state.CursorKey!, state.CursorVersionId!);
                await minioFixture.Storage.Delete(removedMarker.Value.Key, removedMarker.Value.Generation);
            }

            if (state.CursorKey is null)
            {
                break;
            }
        }

        StorageOrphanCleanupState completed = await db.StorageOrphanCleanupStates.AsNoTracking().SingleAsync();
        completed.CursorKey.Should().BeNull("observed provider markers: {0}", string.Join(" | ", observedCursors));
        observedCursors.Should().HaveCountGreaterThanOrEqualTo(5);
        (await ListForKey(laterOrphanKey)).Should().BeEmpty();
        List<StorageObjectInfo> sameKeyVersions = await ListForKey(sameKey);
        sameKeyVersions.Select(item => item.VersionId).Should().Contain(olderGeneration).And.Contain(newerGeneration);
        foreach ((string Key, string Generation) reference in referenced)
        {
            if (removedMarker == (reference.Key, reference.Generation))
            {
                continue;
            }

            (await ListForKey(reference.Key)).Should().Contain(item => item.VersionId == reference.Generation);
        }
    }

    [Fact]
    public async Task RunCleanup_AcrossBoundedSweeps_ShouldResumeAfterRestartDeleteLaterOrphansAndWrapSafely()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var runId = Guid.NewGuid().ToString("N");
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: $"Orphan scan {runId}");
        db.Assets.Add(asset);

        var referenced = new List<(string Key, string Generation)>();
        for (var index = 0; index < 251; index++)
        {
            var key = $"assets/0000/{runId}/{index:D4}";
            var generation = await Upload(key);
            AssetVersion version = TestData.CreateAssetVersion(
                asset.Id, key, versionNumber: index + 1, isCurrent: index == 250,
                processingStatus: AssetVersionProcessingStatus.READY);
            version.StorageVersionId = generation;
            db.AssetVersions.Add(version);
            referenced.Add((key, generation));
        }

        var laterOrphanKey = $"assets/8000/{runId}/orphan";
        _ = await Upload(laterOrphanKey);

        var protectedKey = $"assets/9000/{runId}/protected";
        _ = await Upload(protectedKey);
        var protectedGeneration = await Upload(protectedKey);
        AssetVersion protectedVersion = TestData.CreateAssetVersion(
            asset.Id, protectedKey, versionNumber: 252, isCurrent: false,
            processingStatus: AssetVersionProcessingStatus.READY);
        protectedVersion.StorageVersionId = protectedGeneration;
        db.AssetVersions.Add(protectedVersion);

        var multiGenerationKey = $"assets/zzzz/{runId}/generations";
        var oldGeneration = await Upload(multiGenerationKey);
        await Task.Delay(TimeSpan.FromMilliseconds(1100));
        var newGeneration = await Upload(multiGenerationKey);
        await db.SaveChangesAsync();

        List<StorageObjectInfo> generations = await ListForKey(multiGenerationKey);
        DateTimeOffset oldModified = generations.Single(item => item.VersionId == oldGeneration).LastModified!.Value;
        DateTimeOffset newModified = generations.Single(item => item.VersionId == newGeneration).LastModified!.Value;
        newModified.Should().BeAfter(oldModified);
        DateTimeOffset fixedNow = oldModified + TimeSpan.FromTicks((newModified - oldModified).Ticks / 2) + TimeSpan.FromDays(1);

        // This instance stops after 250 objects. Next instance must obtain persisted cursor after a process restart.
        await using (ServiceProvider firstProcess = CreateProvider(db))
        {
            await CreateWorker(firstProcess, fixedNow).RunCleanup(CancellationToken.None);
        }

        StorageOrphanCleanupState saved = await db.StorageOrphanCleanupStates.SingleAsync();
        saved.CursorKey.Should().Be(referenced[249].Key);
        saved.CursorVersionId.Should().Be(referenced[249].Generation);
        (await ListForKey(laterOrphanKey)).Should().ContainSingle();

        await using (ServiceProvider restartedProcess = CreateProvider(db))
        {
            await CreateWorker(restartedProcess, fixedNow).RunCleanup(CancellationToken.None);
        }

        // Second sweep reached end-of-list, removed only eligible exact generations, and reset cursor for wraparound.
        saved = await db.StorageOrphanCleanupStates.AsNoTracking().SingleAsync();
        saved.CursorKey.Should().BeNull();
        (await ListForKey(laterOrphanKey)).Should().BeEmpty();
        (await ListForKey(protectedKey)).Select(item => item.VersionId).Should().Contain(protectedGeneration);
        (await ListForKey(protectedKey)).Should().HaveCount(2);
        List<StorageObjectInfo> remainingGenerations = await ListForKey(multiGenerationKey);
        remainingGenerations.Should().ContainSingle().Which.VersionId.Should().Be(newGeneration);
        remainingGenerations.Should().NotContain(item => item.VersionId == oldGeneration);

        await using (ServiceProvider wrappedProcess = CreateProvider(db))
        {
            await CreateWorker(wrappedProcess, fixedNow).RunCleanup(CancellationToken.None);
        }

        saved = await db.StorageOrphanCleanupStates.AsNoTracking().SingleAsync();
        saved.CursorKey.Should().Be(referenced[249].Key);
        (await ListForKey(laterOrphanKey)).Should().BeEmpty();
        foreach ((string Key, string Generation) reference in referenced)
        {
            (await ListForKey(reference.Key)).Should().ContainSingle().Which.VersionId.Should().Be(reference.Generation);
        }
    }

    [Fact]
    public async Task StateStore_TryAcquire_WhenTwoProcessesCompete_ShouldGrantOnlyOneLease()
    {
        await using ApplicationDbContext setup = await fixture.CreateCleanDbContext();
        await using ApplicationDbContext firstDb = fixture.CreateDbContext();
        await using ApplicationDbContext secondDb = fixture.CreateDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StorageOrphanCleanupStateStore first = new(firstDb);
        StorageOrphanCleanupStateStore second = new(secondDb);

        StorageOrphanCleanupLease?[] leases = await Task.WhenAll(
            first.TryAcquire(now, TimeSpan.FromMinutes(30)),
            second.TryAcquire(now, TimeSpan.FromMinutes(30)));

        leases.Count(lease => lease is not null).Should().Be(1);
        StorageOrphanCleanupLease held = leases.Single(lease => lease is not null)!;
        await first.Release(held.LeaseId, resetCursor: false);
    }

    [Fact]
    public async Task StateStore_SaveCursor_WhenExpiredLeaseIsReplaced_ShouldRejectStaleOwner()
    {
        await using ApplicationDbContext setup = await fixture.CreateCleanDbContext();
        await using ApplicationDbContext firstDb = fixture.CreateDbContext();
        await using ApplicationDbContext secondDb = fixture.CreateDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        StorageOrphanCleanupStateStore first = new(firstDb);
        StorageOrphanCleanupStateStore second = new(secondDb);

        StorageOrphanCleanupLease oldLease = (await first.TryAcquire(now, TimeSpan.FromSeconds(1)))!;
        StorageOrphanCleanupLease newLease = (await second.TryAcquire(now.AddSeconds(2), TimeSpan.FromMinutes(1)))!;
        var staleAdvance = await first.SaveCursor(oldLease.LeaseId,
            new StorageObjectCursor("assets/stale", "old-generation"), now.AddSeconds(2), TimeSpan.FromMinutes(1));
        var currentAdvance = await second.SaveCursor(newLease.LeaseId,
            new StorageObjectCursor("assets/current", "current-generation"), now.AddSeconds(2), TimeSpan.FromMinutes(1));

        staleAdvance.Should().BeFalse();
        currentAdvance.Should().BeTrue();
        StorageOrphanCleanupState state = await secondDb.StorageOrphanCleanupStates.AsNoTracking().SingleAsync();
        state.CursorKey.Should().Be("assets/current");
        state.CursorVersionId.Should().Be("current-generation");
        await second.Release(newLease.LeaseId, resetCursor: false);
    }

    private async Task<string> Upload(string key)
    {
        var data = "ASBL"u8.ToArray();
        var generation = await minioFixture.Storage.UploadVersioned(key, new MemoryStream(data), data.Length);
        generation.Should().NotBeNullOrWhiteSpace();
        return generation;
    }

    private async Task<List<StorageObjectInfo>> ListForKey(string key)
    {
        List<StorageObjectInfo> result = [];
        await foreach (StorageObjectInfo item in minioFixture.Storage.ListObjects(key, CancellationToken.None))
        {
            if (string.Equals(item.Key, key, StringComparison.Ordinal))
            {
                result.Add(item);
            }
        }
        return result;
    }

    private ServiceProvider CreateProvider(ApplicationDbContext db)
    {
        ServiceCollection services = new();
        services.AddScoped(_ => db);
        services.AddScoped<IAssetStorageService>(_ => minioFixture.Storage);
        services.AddScoped<IAssetStore>(_ => new AssetStore(db));
        services.AddScoped<IStorageOrphanCleanupStateStore>(_ => new StorageOrphanCleanupStateStore(db));
        services.AddScoped<IHostEnvironment>(_ => Substitute.For<IHostEnvironment>());
        return services.BuildServiceProvider();
    }

    private static StorageOrphanCleanupWorker CreateWorker(ServiceProvider provider, DateTimeOffset now)
    {
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        return new StorageOrphanCleanupWorker(
            provider.GetRequiredService<IServiceScopeFactory>(), environment,
            NullLogger<StorageOrphanCleanupWorker>.Instance, new FixedTimeProvider(now));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
