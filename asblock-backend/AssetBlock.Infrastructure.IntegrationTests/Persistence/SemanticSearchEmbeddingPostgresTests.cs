using System.Data.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence;

[Collection(nameof(PostgresStoreCollection))]
public sealed class SemanticSearchEmbeddingPostgresTests(PostgresFixture fixture)
{
    private const string VALID_HEX_64 = "e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6";
    private const string VALID_DIGEST = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6";
    private const int DIMENSION_768 = 768;

    private readonly EmbeddingOptions _defaultOptions = new()
    {
        Enabled = true,
        Provider = "Ollama",
        BaseUrl = "http://127.0.0.1:11434",
        Model = "embeddinggemma:300m-qat-q8_0",
        Revision = "manifest-e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
        Digest = VALID_DIGEST,
        Dimension = DIMENSION_768,
        ContentSchemaVersion = "asset-public-metadata-v1"
    };

    private sealed record SeedData(User Author, Category Category, Asset Asset, AssetVersion Version);

    [Fact]
    public async Task Finalize_AtomicCommitted_ShouldUpsertEmbeddingMarkJobSucceededAndInvalidateCache()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ICacheService cache = Substitute.For<ICacheService>();
        var finalizer = new AssetEmbeddingFinalizer(db, cache, NullLogger<AssetEmbeddingFinalizer>.Instance);

        SeedData seed = await SeedAsset(db);

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(seed.Asset.Title, seed.Asset.Description, seed.Category.Name, null);
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);
        var leaseToken = Guid.NewGuid();

        var job = new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.RUNNING,
            Stage = "RUNNING",
            AttemptCount = 1,
            MaxAttempts = 3,
            AvailableAt = DateTimeOffset.UtcNow,
            LeaseOwner = "worker-1",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Payload = "{}",
            InputHash = canonical.ContentHash,
            ModelKey = modelKey
        };
        db.AssetProcessingJobs.Add(job);
        await db.SaveChangesAsync();

        var vector = new float[DIMENSION_768];
        vector[0] = 0.5f;
        vector[1] = 0.5f;

        var parameters = new FinalizeEmbeddingParameters(
            job.Id,
            leaseToken,
            seed.Asset.Id,
            seed.Version.Id,
            seed.Asset.SearchRevision,
            canonical.ContentHash,
            modelKey,
            "Ollama",
            _defaultOptions.Model,
            _defaultOptions.Revision,
            _defaultOptions.Digest,
            DIMENSION_768,
            AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION,
            vector);

        EmbeddingFinalizationStatus status = await finalizer.Finalize(parameters);
        status.Should().Be(EmbeddingFinalizationStatus.COMMITTED);

        // Verify job state
        AssetProcessingJob updatedJob = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        updatedJob.Status.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        updatedJob.Stage.Should().Be("SUCCEEDED");
        updatedJob.LeaseToken.Should().BeNull();
        updatedJob.LeaseOwner.Should().BeNull();

        // Verify embedding persisted
        AssetEmbedding embedding = await db.AssetEmbeddings.SingleAsync(e => e.AssetId == seed.Asset.Id);
        embedding.SourceRevision.Should().Be(seed.Asset.SearchRevision);
        embedding.ContentHash.Should().Be(canonical.ContentHash);
        embedding.Dimension.Should().Be(DIMENSION_768);

        // Verify cache invalidated
        await cache.Received(1).RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Finalize_WhenLeaseLost_ShouldReturnLeaseLostAndNotUpsert()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ICacheService cache = Substitute.For<ICacheService>();
        var finalizer = new AssetEmbeddingFinalizer(db, cache, NullLogger<AssetEmbeddingFinalizer>.Instance);

        SeedData seed = await SeedAsset(db);

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(seed.Asset.Title, seed.Asset.Description, seed.Category.Name, null);
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);
        var originalLeaseToken = Guid.NewGuid();
        var wrongLeaseToken = Guid.NewGuid();

        var job = new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.RUNNING,
            Stage = "RUNNING",
            AttemptCount = 1,
            MaxAttempts = 3,
            AvailableAt = DateTimeOffset.UtcNow,
            LeaseOwner = "worker-1",
            LeaseToken = originalLeaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Payload = "{}",
            InputHash = canonical.ContentHash,
            ModelKey = modelKey
        };
        db.AssetProcessingJobs.Add(job);
        await db.SaveChangesAsync();

        var vector = new float[DIMENSION_768];
        vector[0] = 0.5f;

        var parameters = new FinalizeEmbeddingParameters(
            job.Id,
            wrongLeaseToken, // Wrong lease token!
            seed.Asset.Id,
            seed.Version.Id,
            seed.Asset.SearchRevision,
            canonical.ContentHash,
            modelKey,
            "Ollama",
            _defaultOptions.Model,
            _defaultOptions.Revision,
            _defaultOptions.Digest,
            DIMENSION_768,
            AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION,
            vector);

        EmbeddingFinalizationStatus status = await finalizer.Finalize(parameters);
        status.Should().Be(EmbeddingFinalizationStatus.LEASE_LOST);

        // Job was NOT modified
        AssetProcessingJob loadedJob = await db.AssetProcessingJobs.SingleAsync(j => j.Id == job.Id);
        loadedJob.Status.Should().Be(AssetProcessingJobStatus.RUNNING);

        // No embedding was created
        var hasEmbedding = await db.AssetEmbeddings.AnyAsync(e => e.AssetId == seed.Asset.Id);
        hasEmbedding.Should().BeFalse();

        await cache.DidNotReceive().RemoveByPrefix(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Finalize_MonotonicUpsert_ShouldNeverOverwriteWithOlderRevision()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ICacheService cache = Substitute.For<ICacheService>();
        var finalizer = new AssetEmbeddingFinalizer(db, cache, NullLogger<AssetEmbeddingFinalizer>.Instance);

        SeedData seed = await SeedAsset(db);

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(seed.Asset.Title, seed.Asset.Description, seed.Category.Name, null);
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);

        // Seed existing embedding with SourceRevision = 5
        var existingEmbedding = new AssetEmbedding
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            ModelKey = modelKey,
            Provider = "Ollama",
            ModelId = _defaultOptions.Model,
            ModelRevision = _defaultOptions.Revision,
            ModelDigest = _defaultOptions.Digest,
            Dimension = DIMENSION_768,
            ContentSchemaVersion = AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION,
            SourceRevision = 5L,
            ContentHash = VALID_HEX_64,
            Embedding = new Pgvector.Vector(new float[DIMENSION_768])
        };
        db.AssetEmbeddings.Add(existingEmbedding);

        // Active job targeting older revision 4
        var leaseToken = Guid.NewGuid();
        var job = new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.RUNNING,
            Stage = "RUNNING",
            AttemptCount = 1,
            MaxAttempts = 3,
            AvailableAt = DateTimeOffset.UtcNow,
            LeaseOwner = "worker-1",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Payload = "{}",
            InputHash = canonical.ContentHash,
            ModelKey = modelKey
        };
        db.AssetProcessingJobs.Add(job);
        await db.SaveChangesAsync();

        var vector = new float[DIMENSION_768];
        vector[0] = 0.9f;

        var parameters = new FinalizeEmbeddingParameters(
            job.Id,
            leaseToken,
            seed.Asset.Id,
            seed.Version.Id,
            4L, // older than existing 5L
            canonical.ContentHash,
            modelKey,
            "Ollama",
            _defaultOptions.Model,
            _defaultOptions.Revision,
            _defaultOptions.Digest,
            DIMENSION_768,
            AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION,
            vector);

        EmbeddingFinalizationStatus status = await finalizer.Finalize(parameters);
        status.Should().Be(EmbeddingFinalizationStatus.COMMITTED);

        // Verify the existing embedding was NOT replaced by the older revision 4
        AssetEmbedding currentEmbedding = await db.AssetEmbeddings.SingleAsync(e => e.AssetId == seed.Asset.Id);
        currentEmbedding.SourceRevision.Should().Be(5L);
        currentEmbedding.ContentHash.Should().Be(VALID_HEX_64);
    }

    [Fact]
    public async Task JobStore_ConcurrentDuplicateEnqueue_PartialIndexAbsorbsConflict()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));

        SeedData seed = await SeedAsset(db);

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(seed.Asset.Title, seed.Asset.Description, seed.Category.Name, null);
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);
        var payload = new EmbeddingGenerationPayload(
            seed.Asset.Id,
            seed.Version.Id,
            seed.Asset.SearchRevision,
            canonical.ContentHash,
            modelKey,
            AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION);

        // 1st enqueue succeeds
        Guid firstJobId = await jobStore.Enqueue(
            seed.Asset.Id,
            seed.Version.Id,
            AssetProcessingJobType.EMBEDDING_GENERATION,
            1,
            TimeSpan.Zero,
            payload);

        firstJobId.Should().NotBeEmpty();

        // 2nd enqueue with same parameters while 1st is active returns the existing JobId without throwing
        Guid secondJobId = await jobStore.Enqueue(
            seed.Asset.Id,
            seed.Version.Id,
            AssetProcessingJobType.EMBEDDING_GENERATION,
            1,
            TimeSpan.Zero,
            payload);

        secondJobId.Should().Be(firstJobId);

        var totalJobs = await db.AssetProcessingJobs.CountAsync(j => j.AssetId == seed.Asset.Id && j.Type == AssetProcessingJobType.EMBEDDING_GENERATION);
        totalJobs.Should().Be(1);
    }

    [Fact]
    public async Task JobStore_InternalJobIsolation_EmbeddingJobsNeverExposedToSellersOrRealtime()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));

        SeedData seed = await SeedAsset(db);

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(seed.Asset.Title, seed.Asset.Description, seed.Category.Name, null);
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);
        var payload = new EmbeddingGenerationPayload(
            seed.Asset.Id,
            seed.Version.Id,
            seed.Asset.SearchRevision,
            canonical.ContentHash,
            modelKey,
            AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION);

        Guid embeddingJobId = await jobStore.Enqueue(
            seed.Asset.Id,
            seed.Version.Id,
            AssetProcessingJobType.EMBEDDING_GENERATION,
            1,
            TimeSpan.Zero,
            payload);

        Guid inspectionJobId = await jobStore.Enqueue(
            seed.Asset.Id,
            seed.Version.Id,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            TimeSpan.Zero,
            new ArchiveInspectionPayload());

        embeddingJobId.Should().NotBeEmpty();
        inspectionJobId.Should().NotBeEmpty();

        // 1. GetJobsForAsset must exclude EMBEDDING_GENERATION
        IReadOnlyList<AssetProcessingJobDto>? assetJobs = await jobStore.GetJobsForAsset(seed.Asset.Id, seed.Author.Id);
        assetJobs.Should().NotBeNull();
        assetJobs.Select(j => j.Type).Should().Contain(AssetProcessingJobType.ARCHIVE_INSPECTION);
        assetJobs.Select(j => j.Type).Should().NotContain(AssetProcessingJobType.EMBEDDING_GENERATION);

        // 2. GetJobsForVersion must exclude EMBEDDING_GENERATION
        IReadOnlyList<AssetProcessingJobDto>? versionJobs = await jobStore.GetJobsForVersion(seed.Version.Id, seed.Author.Id);
        versionJobs.Should().NotBeNull();
        versionJobs.Select(j => j.Type).Should().Contain(AssetProcessingJobType.ARCHIVE_INSPECTION);
        versionJobs.Select(j => j.Type).Should().NotContain(AssetProcessingJobType.EMBEDDING_GENERATION);

        // 3. GetRealtimeState must return null for EMBEDDING_GENERATION
        AssetProcessingJobRealtimeState? realtimeState = await jobStore.GetRealtimeState(embeddingJobId);
        realtimeState.Should().BeNull();
    }

    [Fact]
    public async Task MutationEnqueueRules_AssetUpdate_OnlyRelevantChangesBumpRevisionAndEnqueue()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));
        var assetStore = new AssetStore(db, jobStore, Microsoft.Extensions.Options.Options.Create(_defaultOptions));

        SeedData seed = await SeedAsset(db);
        var initialRevision = seed.Asset.SearchRevision;

        // 1. Price-only change: must NOT bump revision and must NOT enqueue
        await assetStore.Update(
            seed.Asset.Id,
            title: seed.Asset.Title,
            description: seed.Asset.Description,
            price: 99.99m,
            categoryId: seed.Category.Id);

        Asset priceUpdated = await assetStore.GetById(seed.Asset.Id) ?? throw new InvalidOperationException();
        priceUpdated.SearchRevision.Should().Be(initialRevision);

        var embeddingJobCountAfterPrice = await db.AssetProcessingJobs.CountAsync(j => j.AssetId == seed.Asset.Id && j.Type == AssetProcessingJobType.EMBEDDING_GENERATION);
        embeddingJobCountAfterPrice.Should().Be(0);

        // 2. Title change: MUST bump revision and MUST enqueue
        await assetStore.Update(
            seed.Asset.Id,
            title: "Brand New Title",
            description: seed.Asset.Description,
            price: 99.99m,
            categoryId: seed.Category.Id);

        Asset titleUpdated = await assetStore.GetById(seed.Asset.Id) ?? throw new InvalidOperationException();
        titleUpdated.SearchRevision.Should().Be(initialRevision + 1);

        var embeddingJobCountAfterTitle = await db.AssetProcessingJobs.CountAsync(j => j.AssetId == seed.Asset.Id && j.Type == AssetProcessingJobType.EMBEDDING_GENERATION);
        embeddingJobCountAfterTitle.Should().Be(1);
    }

    [Fact]
    public async Task MutationEnqueueRules_TagsAndBulkIncrement_BumpsRevisionsCorrectly()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));
        var assetStore = new AssetStore(db, jobStore, Microsoft.Extensions.Options.Options.Create(_defaultOptions));
        var categoryStore = new CategoryStore(db, NullLogger<CategoryStore>.Instance);
        var tagStore = new TagStore(db);

        SeedData seed = await SeedAsset(db);
        var initialRevision = seed.Asset.SearchRevision;

        var tagId = Guid.NewGuid();
        db.Tags.Add(new Tag { Id = tagId, Name = "SciFi", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        // 1. Add tag to READY asset -> bumps revision & enqueues embedding job
        var addResult = await assetStore.TryAddTag(seed.Asset.Id, tagId);
        addResult.Should().BeTrue();

        Asset afterAddTag = await assetStore.GetById(seed.Asset.Id) ?? throw new InvalidOperationException();
        afterAddTag.SearchRevision.Should().Be(initialRevision + 1);

        // 2. Add duplicate tag -> returns false, does not bump revision
        var addDuplicateResult = await assetStore.TryAddTag(seed.Asset.Id, tagId);
        addDuplicateResult.Should().BeFalse();

        Asset afterDupTag = await assetStore.GetById(seed.Asset.Id) ?? throw new InvalidOperationException();
        afterDupTag.SearchRevision.Should().Be(initialRevision + 1);

        // 3. Remove tag -> bumps revision
        var removeResult = await assetStore.RemoveTag(seed.Asset.Id, tagId);
        removeResult.Should().BeTrue();

        Asset afterRemoveTag = await assetStore.GetById(seed.Asset.Id) ?? throw new InvalidOperationException();
        afterRemoveTag.SearchRevision.Should().Be(initialRevision + 2);

        // 4. Bulk increment via category
        await categoryStore.BulkIncrementAssetSearchRevision(seed.Category.Id);
        Asset afterCategoryBulk = await assetStore.GetById(seed.Asset.Id) ?? throw new InvalidOperationException();
        afterCategoryBulk.SearchRevision.Should().Be(initialRevision + 3);

        // 5. Bulk increment via tag
        _ = await assetStore.TryAddTag(seed.Asset.Id, tagId);
        await tagStore.BulkIncrementAssetSearchRevision(tagId);
        Asset afterTagBulk = await assetStore.GetById(seed.Asset.Id) ?? throw new InvalidOperationException();
        afterTagBulk.SearchRevision.Should().Be(initialRevision + 5);
    }

    [Fact]
    public async Task BackfillCoordinator_BoundedCycle_RespectsMax50AndCooldown()
    {
        EmbeddingBackfillCoordinator.ResetFallbackCursors();
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        generator.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true, null, VALID_DIGEST));

        var coordinator = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance);

        SeedData seed = await SeedAsset(db);

        // Seed 54 additional assets with READY versions and no embeddings (total = 1 + 54 = 55)
        DateTimeOffset now = DateTimeOffset.UtcNow;
        for (var i = 0; i < 54; i++)
        {
            var aid = Guid.NewGuid();
            var vid = Guid.NewGuid();
            db.Assets.Add(new Asset
            {
                Id = aid,
                AuthorId = seed.Author.Id,
                CategoryId = seed.Category.Id,
                Title = $"Backfill Asset {i}",
                Price = 10m,
                CreatedAt = now
            });
            db.AssetVersions.Add(new AssetVersion
            {
                Id = vid,
                AssetId = aid,
                VersionNumber = 1,
                IsCurrent = true,
                StorageKey = $"key_{i}",
                FileName = $"file_{i}",
                ContentLength = 100,
                ContentSha256 = $"hash_{i}",
                ReleaseNotes = "notes",
                LicenseCode = AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal",
                LicenseTerms = "Terms",
                ProcessingStatus = AssetVersionProcessingStatus.READY,
                ProcessingUpdatedAt = now,
                CreatedAt = now
            });
        }
        await db.SaveChangesAsync();

        // 1. Run backfill cycle: should enqueue exactly 50 jobs (bounded limit)
        var enqueued = await coordinator.RunBackfillCycle();
        enqueued.Should().Be(50);

        // 2. Cooldown check: mark one job as FAILED recently (within 1 hour)
        var failedAssetId = Guid.NewGuid();
        var failedVersionId = Guid.NewGuid();
        db.Assets.Add(new Asset
        {
            Id = failedAssetId,
            AuthorId = seed.Author.Id,
            CategoryId = seed.Category.Id,
            Title = "Failed Asset",
            Price = 10m,
            CreatedAt = now
        });
        db.AssetVersions.Add(new AssetVersion
        {
            Id = failedVersionId,
            AssetId = failedAssetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "failed_key",
            FileName = "failed_file",
            ContentLength = 100,
            ContentSha256 = "failed_hash",
            ReleaseNotes = "notes",
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "Terms",
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = now,
            CreatedAt = now
        });
        CanonicalPublicMetadataResult failedCanonical = AssetPublicMetadataCanonicalizer.Canonicalize(
            "Failed Asset", null, seed.Category.Name, null);

        db.AssetProcessingJobs.Add(new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = failedAssetId,
            AssetVersionId = failedVersionId,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.FAILED,
            Stage = "FAILED",
            AttemptCount = 3,
            MaxAttempts = 3,
            AvailableAt = now.AddMinutes(-10),
            CompletedAt = now.AddMinutes(-10),
            Payload = "{}",
            InputHash = failedCanonical.ContentHash,
            ModelKey = EmbeddingModelKey.Compute(_defaultOptions),
            CreatedAt = now.AddMinutes(-10),
            UpdatedAt = now.AddMinutes(-10)
        });
        await db.SaveChangesAsync();

        // Second cycle: remaining 5 from the 55 should be enqueued, but the failed asset (within 1h cooldown) must be skipped!
        var secondEnqueued = await coordinator.RunBackfillCycle();
        secondEnqueued.Should().Be(5); // exactly the remaining 5 from the original 55

        // Third cycle: 0 enqueued because only the failed asset remains and it is in 1h cooldown
        var thirdEnqueued = await coordinator.RunBackfillCycle();
        thirdEnqueued.Should().Be(0);
    }

    [Fact]
    public async Task BackfillCoordinator_WhenActiveOldHashJobExists_EnqueuesNewHashJob()
    {
        EmbeddingBackfillCoordinator.ResetFallbackCursors();
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        generator.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true, null, VALID_DIGEST));

        var coordinator = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance);

        SeedData seed = await SeedAsset(db);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);

        // Add active job for an older metadata hash on the same asset
        db.AssetProcessingJobs.Add(new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.QUEUED,
            Stage = "QUEUED",
            AttemptCount = 0,
            MaxAttempts = 3,
            AvailableAt = now,
            Payload = "{}",
            InputHash = new string('0', 64),
            ModelKey = modelKey,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        // Run backfill cycle: the active job for the old hash should NOT block enqueuing a job for the new hash
        var enqueued = await coordinator.RunBackfillCycle();
        enqueued.Should().Be(1);

        CanonicalPublicMetadataResult currentCanonical = AssetPublicMetadataCanonicalizer.Canonicalize(
            seed.Asset.Title, seed.Asset.Description, seed.Category.Name, null);

        AssetProcessingJob? newJob = await db.AssetProcessingJobs
            .FirstOrDefaultAsync(j => j.AssetId == seed.Asset.Id && j.InputHash == currentCanonical.ContentHash);
        newJob.Should().NotBeNull();
        newJob.Status.Should().Be(AssetProcessingJobStatus.QUEUED);
    }

    [Fact]
    public async Task BackfillCoordinator_WhenMoreThan500SkippedCandidatesExist_RotatingCursorResumesAndEnqueuesEligibleAssetInSubsequentCycle()
    {
        EmbeddingBackfillCoordinator.ResetFallbackCursors();
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        generator.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true, null, VALID_DIGEST));

        var coordinator = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var author = new User
        {
            Id = Guid.NewGuid(),
            Username = "author_" + Guid.NewGuid().ToString("N")[..8],
            Email = Guid.NewGuid().ToString("N")[..8] + "@test.com",
            PasswordHash = "hash",
            Role = AppRoles.USER,
            CreatedAt = now
        };

        var category = new Category
        {
            Id = Guid.NewGuid(),
            Name = "Category " + Guid.NewGuid().ToString("N")[..6],
            Slug = "cat-" + Guid.NewGuid().ToString("N")[..6],
            CreatedAt = now
        };

        db.Users.Add(author);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);

        const int skippedCount = 505;
        var assets = new List<Asset>(skippedCount + 1);
        var versions = new List<AssetVersion>(skippedCount + 1);
        var jobs = new List<AssetProcessingJob>(skippedCount);

        for (var i = 1; i <= skippedCount; i++)
        {
            var assetId = Guid.NewGuid();
            var versionId = Guid.NewGuid();
            assets.Add(new Asset
            {
                Id = assetId,
                AuthorId = author.Id,
                CategoryId = category.Id,
                Title = $"Asset {i}",
                Price = 10m,
                SearchRevision = 1,
                CreatedAt = now.AddMinutes(-i),
                UpdatedAt = now.AddMinutes(-i)
            });
            versions.Add(new AssetVersion
            {
                Id = versionId,
                AssetId = assetId,
                VersionNumber = 1,
                IsCurrent = true,
                StorageKey = $"key_{i}",
                FileName = $"file_{i}",
                ContentLength = 100,
                ContentSha256 = $"hash_{i}",
                ReleaseNotes = "notes",
                LicenseCode = AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal",
                LicenseTerms = "Terms",
                ProcessingStatus = AssetVersionProcessingStatus.READY,
                ProcessingUpdatedAt = now,
                CreatedAt = now
            });
            CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(
                $"Asset {i}", null, category.Name, null);
            jobs.Add(new AssetProcessingJob
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                AssetVersionId = versionId,
                Type = AssetProcessingJobType.EMBEDDING_GENERATION,
                DefinitionVersion = 1,
                Status = AssetProcessingJobStatus.QUEUED,
                Stage = "QUEUED",
                AttemptCount = 0,
                MaxAttempts = 3,
                AvailableAt = now,
                Payload = "{}",
                InputHash = canonical.ContentHash,
                ModelKey = modelKey,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        // Asset 506 is eligible (no active job, no cooldown) and ordered after the first 505 assets
        var eligibleAssetId = Guid.NewGuid();
        var eligibleVersionId = Guid.NewGuid();
        assets.Add(new Asset
        {
            Id = eligibleAssetId,
            AuthorId = author.Id,
            CategoryId = category.Id,
            Title = "Eligible Asset After 505 Skipped",
            Price = 10m,
            SearchRevision = 1,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1)
        });
        versions.Add(new AssetVersion
        {
            Id = eligibleVersionId,
            AssetId = eligibleAssetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "eligible_key",
            FileName = "eligible_file",
            ContentLength = 100,
            ContentSha256 = "eligible_hash",
            ReleaseNotes = "notes",
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "Terms",
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = now,
            CreatedAt = now
        });

        db.Assets.AddRange(assets);
        db.AssetVersions.AddRange(versions);
        db.AssetProcessingJobs.AddRange(jobs);
        await db.SaveChangesAsync();

        // 1. First cycle: scans up to 500 candidates. All 500 have active jobs and are skipped.
        var firstCycleEnqueued = await coordinator.RunBackfillCycle();
        firstCycleEnqueued.Should().Be(0);

        // 2. Second cycle: rotating cursor resumes from offset 500.
        // It scans candidate 501..505 (skipped due to active jobs) and finds candidate 506 (eligible), enqueuing it!
        var secondCycleEnqueued = await coordinator.RunBackfillCycle();
        secondCycleEnqueued.Should().Be(1);

        AssetProcessingJob? enqueuedJob = await db.AssetProcessingJobs
            .FirstOrDefaultAsync(j => j.AssetId == eligibleAssetId && j.Status == AssetProcessingJobStatus.QUEUED);
        enqueuedJob.Should().NotBeNull();
        enqueuedJob.ModelKey.Should().Be(modelKey);
    }

    [Fact]
    public async Task Finalize_WhenLeaseExpiresWhileWaitingForLock_ShouldReturnLeaseLost()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ICacheService cache = Substitute.For<ICacheService>();
        var finalizer = new AssetEmbeddingFinalizer(db, cache, NullLogger<AssetEmbeddingFinalizer>.Instance);

        SeedData seed = await SeedAsset(db);
        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(seed.Asset.Title, seed.Asset.Description, seed.Category.Name, null);
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);
        var leaseToken = Guid.NewGuid();

        // Lease expires in 1 second
        var job = new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.RUNNING,
            Stage = "RUNNING",
            AttemptCount = 1,
            MaxAttempts = 3,
            AvailableAt = DateTimeOffset.UtcNow,
            LeaseOwner = "worker-1",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1),
            Payload = "{}",
            InputHash = canonical.ContentHash,
            ModelKey = modelKey
        };
        db.AssetProcessingJobs.Add(job);
        await db.SaveChangesAsync();

        var vector = new float[DIMENSION_768];
        vector[0] = 0.5f;
        var parameters = new FinalizeEmbeddingParameters(
            job.Id,
            leaseToken,
            seed.Asset.Id,
            seed.Version.Id,
            seed.Asset.SearchRevision,
            canonical.ContentHash,
            modelKey,
            "Ollama",
            _defaultOptions.Model,
            _defaultOptions.Revision,
            _defaultOptions.Digest,
            DIMENSION_768,
            AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION,
            vector);

        // Lock the job row in a separate DbContext connection
        await using ApplicationDbContext dbLock = fixture.CreateDbContext();
        await using IDbContextTransaction txLock = await dbLock.Database.BeginTransactionAsync();
        _ = await dbLock.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" AS "Value" FROM asset_processing_jobs WHERE "Id" = {0} FOR UPDATE""", job.Id)
            .SingleAsync();

        // Start finalization in background - it will block on FOR UPDATE waiting for dbLock
        Task<EmbeddingFinalizationStatus> finalizeTask = Task.Run(async () => await finalizer.Finalize(parameters));

        // Sleep 1.5 seconds so LeaseExpiresAt (1.0s) has passed
        await Task.Delay(1500);

        // Release lock
        await txLock.RollbackAsync();

        // Now finalization unblocks, acquires lock, gets clock_timestamp() and sees expired lease
        EmbeddingFinalizationStatus status = await finalizeTask;
        status.Should().Be(EmbeddingFinalizationStatus.LEASE_LOST);

        // Verify embedding was NOT persisted
        var hasEmbedding = await db.AssetEmbeddings.AnyAsync(e => e.AssetId == seed.Asset.Id);
        hasEmbedding.Should().BeFalse();
    }

    [Fact]
    public async Task MarkJobNoOp_WhenLeaseExpiresWhileWaitingForLock_ShouldReturnFalse()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ICacheService cache = Substitute.For<ICacheService>();
        var finalizer = new AssetEmbeddingFinalizer(db, cache, NullLogger<AssetEmbeddingFinalizer>.Instance);

        SeedData seed = await SeedAsset(db);
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);
        var leaseToken = Guid.NewGuid();

        // Lease expires in 1 second
        var job = new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.RUNNING,
            Stage = "RUNNING",
            AttemptCount = 1,
            MaxAttempts = 3,
            AvailableAt = DateTimeOffset.UtcNow,
            LeaseOwner = "worker-1",
            LeaseToken = leaseToken,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddSeconds(1),
            Payload = "{}",
            InputHash = VALID_HEX_64,
            ModelKey = modelKey
        };
        db.AssetProcessingJobs.Add(job);
        await db.SaveChangesAsync();

        await using ApplicationDbContext dbLock = fixture.CreateDbContext();
        await using IDbContextTransaction txLock = await dbLock.Database.BeginTransactionAsync();
        _ = await dbLock.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" AS "Value" FROM asset_processing_jobs WHERE "Id" = {0} FOR UPDATE""", job.Id)
            .SingleAsync();

        Task<bool> noOpTask = Task.Run(async () => await finalizer.MarkJobNoOp(job.Id, leaseToken));

        await Task.Delay(1500);

        await txLock.RollbackAsync();

        var marked = await noOpTask;
        marked.Should().BeFalse();

        AssetProcessingJob loadedJob = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
        loadedJob.Status.Should().Be(AssetProcessingJobStatus.RUNNING);
    }

    [Fact]
    public async Task BackfillCoordinator_WhenMetadataOrModelChanges_DoesNotBlockOnOldFailure()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        generator.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true, null, VALID_DIGEST));

        var coordinator = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance);

        SeedData seed = await SeedAsset(db);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);

        // Seed an old failure for an OLD input hash (different from seed.Asset current content hash)
        db.AssetProcessingJobs.Add(new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.FAILED,
            Stage = "FAILED",
            AttemptCount = 3,
            MaxAttempts = 3,
            AvailableAt = now.AddMinutes(-5),
            CompletedAt = now.AddMinutes(-5),
            Payload = "{}",
            InputHash = VALID_HEX_64, // Different from seed.Asset current content hash!
            ModelKey = modelKey,
            CreatedAt = now.AddMinutes(-5),
            UpdatedAt = now.AddMinutes(-5)
        });
        await db.SaveChangesAsync();

        // Backfill should NOT be blocked by the old failure because content hash differs!
        var enqueued = await coordinator.RunBackfillCycle();
        enqueued.Should().Be(1);
    }

    [Fact]
    public async Task BackfillCoordinator_WhenOldHashJobActive_DoesNotBlockNewHashEnqueue()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        generator.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true, null, VALID_DIGEST));

        var coordinator = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance);

        SeedData seed = await SeedAsset(db);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);

        // Seed an active (RUNNING) job for an OLD input hash (VALID_HEX_64)
        db.AssetProcessingJobs.Add(new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            Type = AssetProcessingJobType.EMBEDDING_GENERATION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.RUNNING,
            Stage = "RUNNING",
            AttemptCount = 1,
            MaxAttempts = 3,
            AvailableAt = now,
            LeaseOwner = "worker-old",
            LeaseToken = Guid.NewGuid(),
            LeaseExpiresAt = now.AddMinutes(5),
            Payload = "{}",
            InputHash = VALID_HEX_64, // Different from seed.Asset current content hash!
            ModelKey = modelKey,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        // Active job for old hash must NOT block enqueue of the new hash!
        var enqueued = await coordinator.RunBackfillCycle();
        enqueued.Should().Be(1);

        CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(seed.Asset.Title, seed.Asset.Description, seed.Category.Name, null);
        List<AssetProcessingJob> jobs = await db.AssetProcessingJobs
            .AsNoTracking()
            .Where(j => j.AssetId == seed.Asset.Id && j.Type == AssetProcessingJobType.EMBEDDING_GENERATION)
            .ToListAsync();

        jobs.Should().HaveCount(2);
        jobs.Should().ContainSingle(j => j.Status == AssetProcessingJobStatus.QUEUED && j.InputHash == canonical.ContentHash);
    }

    [Fact]
    public async Task BackfillCoordinator_WhenDbCommitFails_DoesNotAdvanceCursor()
    {
        EmbeddingBackfillCoordinator.ResetFallbackCursors();

        var interceptor = new FailCommitInterceptor();
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext(
            configure: builder => builder.AddInterceptors(interceptor));

        IAssetProcessingJobStore jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        generator.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true, null, VALID_DIGEST));

        ICacheService cache = Substitute.For<ICacheService>();
        cache.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("0");

        var coordinator = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance,
            cacheService: cache);

        await SeedAsset(db);
        var modelKey = EmbeddingModelKey.Compute(_defaultOptions);

        // Enable forced commit failure for the backfill cycle transaction
        interceptor.ShouldFail = true;

        Func<Task> act = async () => await coordinator.RunBackfillCycle();
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Forced DB commit failure*");

        await cache.DidNotReceiveWithAnyArgs().SetString(null!, null!, TimeSpan.Zero);

        var currentOffset = await coordinator.GetCursorOffset(modelKey, CancellationToken.None);
        currentOffset.Should().Be(0);

        List<AssetProcessingJob> jobs = await db.AssetProcessingJobs.AsNoTracking().ToListAsync();
        jobs.Should().BeEmpty();
    }

    [Fact]
    public async Task BackfillCoordinator_CacheReadAndWrite_OccurOutsideDatabaseTransaction()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        IAssetProcessingJobStore jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));
        ITextEmbeddingGenerator generator = Substitute.For<ITextEmbeddingGenerator>();
        generator.CheckModelAvailability(Arg.Any<CancellationToken>())
            .Returns(new ModelVerificationResult(true, null, VALID_DIGEST));

        ICacheService cache = Substitute.For<ICacheService>();
        var readHadTransaction = true;
        var writeHadTransaction = true;

        cache.GetString(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<string?>>(async _ =>
            {
                readHadTransaction = db.Database.CurrentTransaction != null;
                await Task.Delay(50);
                return "0";
            });

        cache.SetString(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(async _ =>
            {
                writeHadTransaction = db.Database.CurrentTransaction != null;
                await Task.Delay(50);
            });

        var coordinator = new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            generator,
            Microsoft.Extensions.Options.Options.Create(_defaultOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance,
            cacheService: cache);

        await SeedAsset(db);

        var enqueued = await coordinator.RunBackfillCycle();
        enqueued.Should().Be(1);

        readHadTransaction.Should().BeFalse("Cache read must occur before opening DB transaction");
        writeHadTransaction.Should().BeFalse("Cache write must occur after committing DB transaction");
    }

    private sealed class FailCommitInterceptor : DbTransactionInterceptor
    {
        public bool ShouldFail { get; set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            if (ShouldFail)
            {
                throw new InvalidOperationException("Forced DB commit failure for testing cursor stability");
            }

            return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
        }
    }

    private static async Task<SeedData> SeedAsset(ApplicationDbContext db)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var authorId = Guid.NewGuid();
        var categoryId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();

        var author = new User
        {
            Id = authorId,
            Username = "author_" + Guid.NewGuid().ToString("N")[..8],
            Email = Guid.NewGuid().ToString("N")[..8] + "@test.com",
            PasswordHash = "hash",
            Role = AppRoles.USER,
            CreatedAt = now
        };

        var category = new Category
        {
            Id = categoryId,
            Name = "Category " + Guid.NewGuid().ToString("N")[..6],
            Slug = "cat-" + Guid.NewGuid().ToString("N")[..6],
            CreatedAt = now
        };

        var asset = new Asset
        {
            Id = assetId,
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = "Original Title",
            Description = "Original Description",
            Price = 10m,
            CreatedAt = now
        };

        var version = new AssetVersion
        {
            Id = versionId,
            AssetId = assetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "key",
            FileName = "file",
            ContentLength = 100,
            ContentSha256 = "abc",
            ReleaseNotes = "notes",
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "Terms",
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = now,
            CreatedAt = now
        };

        db.Users.Add(author);
        db.Categories.Add(category);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        return new SeedData(author, category, asset, version);
    }
}
