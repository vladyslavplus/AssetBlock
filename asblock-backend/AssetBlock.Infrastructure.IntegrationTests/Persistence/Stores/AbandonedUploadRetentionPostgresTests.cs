using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Domain.Core.Primitives.Storage;
using AssetBlock.Infrastructure.HostedServices;
using AssetBlock.Infrastructure.IntegrationTests.Storage;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AbandonedUploadRetentionPostgresTests(PostgresFixture fixture, MinioStorageFixture minioFixture)
    : IClassFixture<MinioStorageFixture>
{
    [Fact]
    public async Task RunCleanup_AfterNormalArchiveMalwareAndAnalysisScheduling_ShouldRetirePayloadAndRetainProof()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: $"Processed abandoned {Guid.NewGuid():N}");
        byte[] payload = [7, 8, 9, 10];
        var contentSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION);
        version.CreatedAt = now.AddDays(-31);
        version.ContentLength = payload.Length;
        version.ContentSha256 = contentSha256;
        var key = $"assets/{asset.Id:N}/v1/processed-abandoned.zip";
        var generation = await minioFixture.Storage.UploadVersioned(key, new MemoryStream(payload), payload.Length);
        generation.Should().NotBeNullOrWhiteSpace();
        version.StorageKey = key;
        version.StorageVersionId = generation;
        asset.CreatedAt = version.CreatedAt;
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);

        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        await db.SaveChangesAsync();

        AssetProcessingJobStore jobs = new(db, NullLogger<AssetProcessingJobStore>.Instance,
            Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));
        AnalysisRunEnqueuer analysisRuns = CreateAnalysisRunEnqueuer(db, chain.PolicyDigest);
        AssetProcessingLifecycleStore lifecycle = new(
            db, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()), analysisRuns);
        await jobs.Enqueue(asset.Id, version.Id, AssetProcessingJobType.ARCHIVE_INSPECTION, 1,
            TimeSpan.Zero, new ArchiveInspectionPayload());
        ClaimedAssetProcessingJob archiveJob = (await jobs.ClaimPendingBatch(1, TimeSpan.FromMinutes(2), "retention-test-worker")).Single();
        var archiveAccepted = await lifecycle.TransitionArchiveInspectionAccepted(
            archiveJob.JobId, archiveJob.LeaseToken, asset.Id, version.Id,
            new ArchiveInspectionResult(1, payload.Length), new BoundedArchiveAnalysisRecord(1, payload.Length,
                "distinctive private README retained in archive metadata",
                new ArchiveAnalysisManifestMetadata([new RecognizedManifestItem(
                    "package.json", "NPM", PackageName: "distinctive-private-manifest") ])),
            new ArchiveValidationEvidence(contentSha256, 1, payload.Length, payload.Length, true,
                ArchiveSafetyInspector.IMPLEMENTATION_IDENTITY), 1);
        archiveAccepted.Should().BeTrue();

        ClaimedAssetProcessingJob malwareJob = (await jobs.ClaimPendingBatch(1, TimeSpan.FromMinutes(2), "retention-test-worker")).Single();
        malwareJob.Type.Should().Be(AssetProcessingJobType.MALWARE_SCAN);
        var malwareAccepted = await lifecycle.TransitionMalwareScanClean(
            malwareJob.JobId, malwareJob.LeaseToken, asset.Id, version.Id, new MalwareScanResult(true),
            new MalwareScanEvidence(contentSha256, payload.Length, true, true, "clamav", "1.0.3", "clamav-test-signatures",
                now, now.AddDays(2)), 1);
        malwareAccepted.Should().BeTrue();

        CodeAnalysisRun run = await db.CodeAnalysisRuns.SingleAsync(r => r.AssetVersionId == version.Id);
        run.State.Should().Be(CodeAnalysisRunState.QUEUED);
        await db.CodeAnalysisRuns.Where(r => r.Id == run.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(r => r.State, CodeAnalysisRunState.FAILED)
                .SetProperty(r => r.CompletedAt, now));
        await db.AssetProcessingJobs.Where(j => j.AnalysisRunId == run.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(j => j.Status, AssetProcessingJobStatus.FAILED)
                .SetProperty(j => j.CompletedAt, now));
        await db.AssetArchiveAnalyses.Where(a => a.AssetVersionId == version.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(a => a.CreatedAt, now.AddMonths(-12).AddSeconds(-1)));

        await using ServiceProvider provider = CreateProvider(db, now);
        await provider.GetRequiredService<AbandonedUploadRetentionWorker>().RunCleanup(CancellationToken.None);

        Asset retainedAsset = await db.Assets.SingleAsync(a => a.Id == asset.Id);
        retainedAsset.DeletedAt.Should().NotBeNull();
        AssetVersion retainedVersion = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id);
        retainedVersion.StorageRetiredAt.Should().NotBeNull();
        retainedVersion.StorageKey.Should().Be(key);
        retainedVersion.StorageVersionId.Should().Be(generation);
        (await db.SecurityEvidenceRecords.CountAsync(e => e.AssetVersionId == version.Id)).Should().Be(2);
        (await db.CodeAnalysisRuns.CountAsync(r => r.Id == run.Id)).Should().Be(1);
        AssetArchiveAnalysis archive = await db.AssetArchiveAnalyses.SingleAsync(a => a.AssetVersionId == version.Id);
        archive.ReadmeContent.Should().Be("distinctive private README retained in archive metadata");
        archive.ManifestMetadata.Should().Contain("distinctive-private-manifest");

        await using (ServiceProvider retentionProvider = CreateAnalysisRetentionProvider(db, now))
        {
            await retentionProvider.GetRequiredService<CodeAnalysisRetentionWorker>().RunCleanup(CancellationToken.None);
        }

        archive = await db.AssetArchiveAnalyses.AsNoTracking().SingleAsync(a => a.AssetVersionId == version.Id);
        archive.ReadmeContent.Should().BeNull();
        archive.ManifestMetadata.Should().BeNull();
        archive.FileCount.Should().Be(1);
        archive.TotalExpandedBytes.Should().Be(payload.Length);
        (await db.SecurityEvidenceRecords.CountAsync(e => e.AssetVersionId == version.Id)).Should().Be(2);
        OutboxMessage message = await db.OutboxMessages.SingleAsync(m => m.Type == OutboxMessageTypes.ASSET_BLOB_DELETE);
        AssetBlobDeleteOutboxHandler handler = new(minioFixture.Storage, new AssetStore(db),
            NullLogger<AssetBlobDeleteOutboxHandler>.Instance);
        await handler.Handle(message, CancellationToken.None);
        await handler.Handle(message, CancellationToken.None);

        List<StorageObjectInfo> remaining = [];
        await foreach (StorageObjectInfo item in minioFixture.Storage.ListObjects("assets/", CancellationToken.None))
        {
            if (item.Key == key && item.VersionId == generation)
            {
                remaining.Add(item);
            }
        }
        remaining.Should().BeEmpty();
        (await db.SecurityEvidenceRecords.CountAsync(e => e.AssetVersionId == version.Id)).Should().Be(2);
    }

    [Fact]
    public async Task RunCleanup_WhenOneHundredOlderUnknownGenerationsExist_ShouldReachLaterKnownUpload()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (User unknownAuthor, Category unknownCategory) = await TestData.SeedAuthorAndCategory(db);
        for (var index = 0; index < 100; index++)
        {
            await SeedUpload(db, now.AddDays(-40).AddMinutes(index), (index % 3) switch
            {
                0 => null,
                1 => " ",
                _ => "null"
            }, unknownAuthor, unknownCategory);
        }
        (Asset knownAsset, _) = await SeedUpload(db, now.AddDays(-31), "later-known-generation", unknownAuthor, unknownCategory);
        await using ServiceProvider provider = CreateProvider(db, now);

        await provider.GetRequiredService<AbandonedUploadRetentionWorker>().RunCleanup(CancellationToken.None);

        (await db.Assets.AnyAsync(a => a.Id == knownAsset.Id)).Should().BeTrue();
        (await db.Assets.SingleAsync(a => a.Id == knownAsset.Id)).DeletedAt.Should().NotBeNull();
        (await db.Assets.CountAsync(a => a.DeletedAt == null)).Should().Be(100);
        (await db.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.ASSET_BLOB_DELETE)).Should().Be(1);
    }

    [Fact]
    public async Task RunCleanup_WhenUploadReachedThirtyDayBoundary_ShouldTombstoneAndEnqueueExactGeneration()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (Asset asset, AssetVersion version) = await SeedUpload(db, now.AddDays(-30), "version-30d");
        await using ServiceProvider provider = CreateProvider(db, now);

        await provider.GetRequiredService<AbandonedUploadRetentionWorker>().RunCleanup(CancellationToken.None);

        (await db.Assets.SingleAsync(a => a.Id == asset.Id)).DeletedAt.Should().NotBeNull();
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id)).StorageRetiredAt.Should().NotBeNull();
        OutboxMessage message = await db.OutboxMessages.SingleAsync(m => m.Type == OutboxMessageTypes.ASSET_BLOB_DELETE);
        message.Payload.Should().Contain(asset.Id.ToString());
        message.Payload.Should().Contain(version.Id.ToString());
        message.Payload.Should().Contain(version.StorageKey);
        message.Payload.Should().Contain("version-30d");
    }

    [Fact]
    public async Task RunCleanup_WhenUploadIsYoungerThanThirtyDays_ShouldRetainAsset()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (Asset asset, _) = await SeedUpload(db, now.AddDays(-30).AddSeconds(1), "version-young");
        await using ServiceProvider provider = CreateProvider(db, now);

        await provider.GetRequiredService<AbandonedUploadRetentionWorker>().RunCleanup(CancellationToken.None);

        (await db.Assets.AnyAsync(a => a.Id == asset.Id)).Should().BeTrue();
        (await db.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunCleanup_WhenStorageGenerationIsUnknown_ShouldRetainAssetAndEmitNoDelete()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (Asset asset, _) = await SeedUpload(db, now.AddDays(-31), null);
        await using ServiceProvider provider = CreateProvider(db, now);

        await provider.GetRequiredService<AbandonedUploadRetentionWorker>().RunCleanup(CancellationToken.None);

        (await db.Assets.AnyAsync(a => a.Id == asset.Id)).Should().BeTrue();
        (await db.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RunCleanup_WhenPurchaseProtectsUpload_ShouldPreserveBuyerEntitlementAndBlobReference()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("retention-buyer", "retention-buyer@example.test");
        db.Users.Add(buyer);
        await db.SaveChangesAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (Asset asset, AssetVersion version) = await SeedUpload(db, now.AddDays(-31), "version-purchased", author, category);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, version.Id), asset.Title, author.Id);
        await db.SaveChangesAsync();
        await using ServiceProvider provider = CreateProvider(db, now);

        await provider.GetRequiredService<AbandonedUploadRetentionWorker>().RunCleanup(CancellationToken.None);

        (await db.Assets.AnyAsync(a => a.Id == asset.Id)).Should().BeTrue();
        (await db.Purchases.AnyAsync(p => p.AssetId == asset.Id)).Should().BeTrue();
        (await db.OutboxMessages.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task RetiredProtectedVersion_ShouldBeRetainedByOutboxAndOrphanSweep()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("retired-protection-buyer", "retired-protection-buyer@example.test");
        db.Users.Add(buyer);
        await db.SaveChangesAsync();
        (Asset asset, AssetVersion version) = await SeedUpload(db, now.AddDays(-31), "retired-protected-generation", author, category);
        var bytes = ")*+"u8.ToArray();
        var key = version.StorageKey;
        var generation = await minioFixture.Storage.UploadVersioned(key, new MemoryStream(bytes), bytes.Length)
            ?? throw new InvalidOperationException("Versioned MinIO upload returned no generation.");
        await db.AssetVersions.Where(v => v.Id == version.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(v => v.StorageVersionId, generation));

        AssetStore retiredStore = new(db);
        await using (IDbContextTransaction tx = await db.Database.BeginTransactionAsync())
        {
            (await retiredStore.GetForUpdate(asset.Id)).Should().NotBeNull();
            await retiredStore.RetireAbandonedUpload(asset.Id, now);
            OutboxStore outbox = new(db, NullLogger<OutboxStore>.Instance, new FixedTimeProvider(now));
            await outbox.Enqueue(OutboxMessageTypes.ASSET_BLOB_DELETE,
                new AssetBlobDeletePayload(asset.Id, version.Id, key, generation));
            await tx.CommitAsync();
        }
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id)).StorageRetiredAt.Should().NotBeNull();
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, version.Id), asset.Title, author.Id);
        await db.SaveChangesAsync();

        OutboxMessage message = await db.OutboxMessages.SingleAsync(m => m.Type == OutboxMessageTypes.ASSET_BLOB_DELETE);
        AssetStore assetStore = retiredStore;
        (await assetStore.ExistsByStorageKey(key)).Should().BeTrue();
        AssetBlobDeleteOutboxHandler handler = new(minioFixture.Storage, assetStore, NullLogger<AssetBlobDeleteOutboxHandler>.Instance);
        Func<Task> protectedDelete = () => handler.Handle(message, CancellationToken.None);
        await protectedDelete.Should().ThrowAsync<InvalidOperationException>();

        ServiceCollection orphanServices = new();
        orphanServices.AddSingleton(db);
        orphanServices.AddSingleton(minioFixture.Storage);
        orphanServices.AddSingleton<IAssetStore>(assetStore);
        orphanServices.AddSingleton<IStorageOrphanCleanupStateStore>(_ => new StorageOrphanCleanupStateStore(db));
        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        orphanServices.AddSingleton(environment);
        await using ServiceProvider orphanProvider = orphanServices.BuildServiceProvider();
        StorageOrphanCleanupWorker orphanWorker = new(orphanProvider.GetRequiredService<IServiceScopeFactory>(), environment,
            NullLogger<StorageOrphanCleanupWorker>.Instance, new FixedTimeProvider(now.AddDays(2)));
        await orphanWorker.RunCleanup(CancellationToken.None);

        List<StorageObjectInfo> retained = [];
        await foreach (StorageObjectInfo item in minioFixture.Storage.ListObjects(key, CancellationToken.None))
        {
            if (item.Key == key && item.VersionId == generation)
            {
                retained.Add(item);
            }
        }
        retained.Should().ContainSingle();
    }

    [Fact]
    public async Task RunCleanup_WhenActiveWorkPublicationCheckoutOrHoldProtectsUpload_ShouldRetainEachAsset()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("retention-control-buyer", "retention-control-buyer@example.test");
        db.Users.Add(buyer);
        await db.SaveChangesAsync();

        var candidates = new List<(Asset Asset, AssetVersion Version)>();
        (Asset activeAsset, AssetVersion activeVersion) = await SeedUpload(db, now.AddDays(-31), "active-processing", author, category);
        candidates.Add((activeAsset, activeVersion));
        db.AssetProcessingJobs.Add(new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = activeAsset.Id,
            AssetVersionId = activeVersion.Id,
            Type = AssetProcessingJobType.MALWARE_SCAN,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.RUNNING,
            Stage = "SCANNING",
            AttemptCount = 1,
            MaxAttempts = 3,
            AvailableAt = now.AddMinutes(-1),
            StartedAt = now.AddMinutes(-1),
            LeaseOwner = "retention-control-worker",
            LeaseToken = Guid.NewGuid(),
            LeaseExpiresAt = now.AddMinutes(1),
            Payload = "{}",
            CreatedAt = now.AddMinutes(-1)
        });

        (Asset checkoutAsset, AssetVersion checkoutVersion) = await SeedUpload(db, now.AddDays(-31), "pending-checkout", author, category);
        candidates.Add((checkoutAsset, checkoutVersion));
        CheckoutIntent pendingIntent = AddCheckoutIntent(db, buyer.Id, checkoutAsset, now, stripeSessionId: null);
        db.CheckoutIntentItems.Add(CreateCheckoutItem(pendingIntent, checkoutAsset, checkoutVersion, author.Id));

        (Asset providerBoundAsset, AssetVersion providerBoundVersion) = await SeedUpload(db, now.AddDays(-31), "provider-bound-checkout", author, category);
        candidates.Add((providerBoundAsset, providerBoundVersion));
        CheckoutIntent providerBoundIntent = AddCheckoutIntent(db, buyer.Id, providerBoundAsset, now,
            $"retention-session-{Guid.NewGuid():N}", CheckoutIntentStatus.CANCELLED);
        db.CheckoutIntentItems.Add(CreateCheckoutItem(providerBoundIntent, providerBoundAsset, providerBoundVersion, author.Id));

        (Asset heldAsset, AssetVersion heldVersion) = await SeedUpload(db, now.AddDays(-31), "reconciliation-hold", author, category);
        candidates.Add((heldAsset, heldVersion));
        CheckoutIntent heldIntent = AddCheckoutIntent(db, buyer.Id, heldAsset, now,
            $"retention-held-session-{Guid.NewGuid():N}", CheckoutIntentStatus.COMPLETED);
        db.CheckoutIntentItems.Add(CreateCheckoutItem(heldIntent, heldAsset, heldVersion, author.Id));
        db.PaidCheckoutReconciliationHolds.Add(new PaidCheckoutReconciliationHold
        {
            Id = Guid.NewGuid(),
            CheckoutIntentId = heldIntent.Id,
            StripeSessionId = heldIntent.StripeSessionId,
            State = PaidCheckoutReconciliationState.HELD,
            SafePaymentFactsJson = "{}",
            ItemIdentitiesJson = "[]",
            CreatedAt = now
        });

        (Asset publishedAsset, AssetVersion publishedVersion, _) = await ApprovedPublicationTestBuilder.SeedApprovedListing(
            db, author, category, "Retention approved publication");
        publishedAsset.CreatedAt = publishedVersion.CreatedAt = now.AddDays(-31);
        await db.AssetVersions.Where(v => v.Id == publishedVersion.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(v => v.StorageVersionId, "approved-publication-generation"));
        candidates.Add((publishedAsset, publishedVersion));
        await db.SaveChangesAsync();

        await using ServiceProvider provider = CreateProvider(db, now);
        await provider.GetRequiredService<AbandonedUploadRetentionWorker>().RunCleanup(CancellationToken.None);

        foreach ((Asset asset, AssetVersion version) in candidates)
        {
            (await db.Assets.SingleAsync(a => a.Id == asset.Id)).DeletedAt.Should().BeNull();
            (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id)).StorageRetiredAt.Should().BeNull();
        }
        (await db.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.ASSET_BLOB_DELETE)).Should().Be(0);
    }

    [Fact]
    public async Task RunCleanup_ThroughOutboxAndVersionedMinio_ShouldRemoveDatabaseAndExactObjectAndReplaySafely()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: $"Integrated removal {Guid.NewGuid():N}");
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, processingStatus: AssetVersionProcessingStatus.READY);
        var payload = new byte[] { 11, 22, 33, 44 };
        var key = $"assets/{asset.Id:N}/v1/integrated-removal.zip";
        var generation = await minioFixture.Storage.UploadVersioned(key, new MemoryStream(payload), payload.Length);
        generation.Should().NotBeNullOrWhiteSpace();
        version.StorageKey = key;
        version.StorageVersionId = generation;
        version.CreatedAt = now.AddDays(-31);
        version.ProcessingUpdatedAt = version.CreatedAt;
        asset.CreatedAt = version.CreatedAt;
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        await using ServiceProvider provider = CreateProvider(db, now);

        await provider.GetRequiredService<AbandonedUploadRetentionWorker>().RunCleanup(CancellationToken.None);
        (await db.Assets.SingleAsync(a => a.Id == asset.Id)).DeletedAt.Should().NotBeNull();
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == version.Id)).StorageRetiredAt.Should().NotBeNull();
        OutboxMessage message = await db.OutboxMessages.SingleAsync(m => m.Type == OutboxMessageTypes.ASSET_BLOB_DELETE);
        AssetBlobDeleteOutboxHandler handler = new(minioFixture.Storage, new AssetStore(db),
            NullLogger<AssetBlobDeleteOutboxHandler>.Instance);

        await handler.Handle(message, CancellationToken.None);
        // A crash after MinIO deletion but before MarkProcessed replays this same exact generation.
        await handler.Handle(message, CancellationToken.None);

        List<StorageObjectInfo> remaining = [];
        await foreach (StorageObjectInfo item in minioFixture.Storage.ListObjects("assets/", CancellationToken.None))
        {
            if (item.Key == key)
            {
                remaining.Add(item);
            }
        }
        remaining.Should().BeEmpty();
    }

    private static async Task<(Asset Asset, AssetVersion Version)> SeedUpload(
        ApplicationDbContext db,
        DateTimeOffset createdAt,
        string? storageVersionId,
        User? knownAuthor = null,
        Category? knownCategory = null)
    {
        User author = knownAuthor ?? (await TestData.SeedAuthorAndCategory(db)).Author;
        Category category = knownCategory ?? await db.Categories.SingleAsync();
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: $"Abandoned {Guid.NewGuid():N}");
        asset.CreatedAt = createdAt;
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, processingStatus: AssetVersionProcessingStatus.READY);
        version.CreatedAt = createdAt;
        version.ProcessingUpdatedAt = createdAt;
        version.StorageVersionId = storageVersionId;
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        return (asset, version);
    }

    private static CheckoutIntent AddCheckoutIntent(
        ApplicationDbContext db,
        Guid buyerId,
        Asset asset,
        DateTimeOffset now,
        string? stripeSessionId,
        CheckoutIntentStatus status = CheckoutIntentStatus.PENDING)
    {
        CheckoutIntent intent = new()
        {
            Id = Guid.NewGuid(),
            UserId = buyerId,
            AssetId = asset.Id,
            ProductTitle = asset.Title,
            AmountTotal = asset.Price,
            Currency = "usd",
            StripeSessionId = stripeSessionId,
            Status = status,
            CreatedAt = now,
            ExpiresAt = now.AddHours(1)
        };
        db.CheckoutIntents.Add(intent);
        return intent;
    }

    private static CheckoutIntentItem CreateCheckoutItem(CheckoutIntent intent, Asset asset, AssetVersion version, Guid sellerId) => new()
    {
        Id = Guid.NewGuid(),
        CheckoutIntentId = intent.Id,
        AssetId = asset.Id,
        AssetVersionId = version.Id,
        SellerId = sellerId,
        Position = 1,
        AssetTitleSnapshot = asset.Title,
        VersionNumber = version.VersionNumber,
        ListPrice = asset.Price,
        AllocatedPrice = asset.Price,
        LicenseCode = AssetLicenseCode.PERSONAL,
        LicenseTemplateVersion = "1.0",
        LicenseDisplayName = "Personal use",
        LicenseTerms = "terms"
    };

    private static ServiceProvider CreateProvider(ApplicationDbContext db, DateTimeOffset now)
    {
        var timeProvider = new FixedTimeProvider(now);
        ServiceCollection services = new();
        services.AddSingleton(db);
        services.AddSingleton<IAssetStore>(_ => new AssetStore(db));
        services.AddSingleton<IPurchaseStore>(_ => new PurchaseStore(db));
        services.AddSingleton<ICheckoutIntentStore>(_ => new CheckoutIntentStore(db, timeProvider));
        services.AddSingleton<ICheckoutReconciliationHoldStore>(_ => new CheckoutReconciliationHoldStore(db));
        services.AddSingleton<IUnitOfWork>(_ => new EfUnitOfWork(db));
        services.AddSingleton<IOutboxStore>(_ => new OutboxStore(db, NullLogger<OutboxStore>.Instance, timeProvider));
        services.AddSingleton(Substitute.For<IHostEnvironment>());
        services.AddSingleton<ILogger<AbandonedUploadRetentionWorker>>(NullLogger<AbandonedUploadRetentionWorker>.Instance);
        services.AddSingleton(timeProvider);
        services.AddSingleton(sp => new AbandonedUploadRetentionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<ILogger<AbandonedUploadRetentionWorker>>(), timeProvider));
        return services.BuildServiceProvider();
    }

    private static ServiceProvider CreateAnalysisRetentionProvider(ApplicationDbContext db, DateTimeOffset now)
    {
        ServiceCollection services = new();
        services.AddSingleton(db);
        services.AddSingleton(Substitute.For<IHostEnvironment>());
        services.AddSingleton<ILogger<CodeAnalysisRetentionWorker>>(NullLogger<CodeAnalysisRetentionWorker>.Instance);
        services.AddSingleton(sp => new CodeAnalysisRetentionWorker(
            sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<IHostEnvironment>(),
            sp.GetRequiredService<ILogger<CodeAnalysisRetentionWorker>>(), new FixedTimeProvider(now)));
        return services.BuildServiceProvider();
    }

    private static AnalysisRunEnqueuer CreateAnalysisRunEnqueuer(ApplicationDbContext db, string policyDigest) =>
        new(db,
            Microsoft.Extensions.Options.Options.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = "code-analysis",
                PolicyKey = "code-analysis-policy-v1",
                PolicyDigest = policyDigest
            }),
            Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()),
            NullLogger<AnalysisRunEnqueuer>.Instance);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
