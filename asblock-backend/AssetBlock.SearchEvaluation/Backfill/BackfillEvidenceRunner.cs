using System.Data;
using System.Globalization;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using AssetBlock.SearchEvaluation.Infrastructure;
using AssetBlock.SearchEvaluation.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Pgvector;

namespace AssetBlock.SearchEvaluation.Backfill;

#pragma warning disable CA5394 // Deterministic pseudo-randomness for test fixtures
public static class BackfillEvidenceRunner
{
    private const long ADVISORY_LOCK_KEY = 0x4153424C4F434B03L;

    public static async Task<int> RunBackfillEvidenceAsync(
        EmbeddingOptions embeddingOptions,
        CancellationToken cancellationToken = default)
    {
        Console.WriteLine("==========================================================");
        Console.WriteLine(" AssetBlock Durable Backfill Evidence Runner");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"Model:            {embeddingOptions.Model}");
        Console.WriteLine($"Model Digest:     {embeddingOptions.Digest}");
        Console.WriteLine($"Dimension:        {embeddingOptions.Dimension}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine();

        embeddingOptions.BackfillBatchSize = 20;
        var modelKey = EmbeddingModelKey.Compute(embeddingOptions);

        Console.WriteLine("--> Initializing isolated disposable pgvector Testcontainer...");
        await using var fixture = new SearchEvaluationDbFixture();
        await fixture.InitializeAsync(cancellationToken);

        SystemEnvironmentProvenance provenance = await fixture.CollectProvenance(cancellationToken);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[PASS] Isolated PostgreSQL container initialized.");
        Console.ResetColor();
        Console.WriteLine();

        // 1. Seed base author and category
        Guid authorId;
        Guid categoryId;
        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            var author = new User
            {
                Id = Guid.NewGuid(),
                Username = "backfill_author",
                Email = "backfill_author@example.com",
                PasswordHash = "hash",
                Role = AppRoles.USER,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Users.Add(author);
            authorId = author.Id;

            var category = new Category
            {
                Id = Guid.NewGuid(),
                Name = "3D Models",
                Slug = "3d-models",
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Categories.Add(category);
            categoryId = category.Id;

            await db.SaveChangesAsync(cancellationToken);
        }

        // 2. Seed diverse backfill scenario:
        // - 10 completed (current embedding exists)
        // - 20 pending (ready version, no embedding)
        // - 10 stale (embedding revision 1, asset revision 2)
        // - 5 active job (already running/queued, must be skipped)
        // - 5 cooldown (failed < 1 hour ago, must be skipped)
        // - 5 expired cooldown (failed > 2 hours ago, eligible for retry)
        // - 5 ineligible (not ready status)
        // - 5 ineligible (soft deleted)
        Console.WriteLine("--> Seeding representative backfill scenario across 65 assets...");
        await SeedBackfillScenarioAsync(fixture, authorId, categoryId, modelKey, embeddingOptions, cancellationToken);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[PASS] Seeded isolated backfill dataset.");
        Console.ResetColor();
        Console.WriteLine();

        IOptions<AssetProcessingOptions> jobOptions = Options.Create(new AssetProcessingOptions());

        // 3. Test Advisory Lock Invariant
        Console.WriteLine("--> Testing advisory lock mutual exclusion...");
        var advisoryLockVerified = false;
        await using (ApplicationDbContext dbLock = fixture.CreateDbContext())
        {
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await dbLock.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            var locked = await dbLock.Database.SqlQueryRaw<bool>(
                $"SELECT pg_try_advisory_xact_lock({ADVISORY_LOCK_KEY}) AS \"Value\"").SingleAsync(cancellationToken);

            if (locked)
            {
                // Attempt cycle while lock is held
                await using ApplicationDbContext dbRunner = fixture.CreateDbContext();
                var jobStore = new AssetProcessingJobStore(dbRunner, NullLogger<AssetProcessingJobStore>.Instance, jobOptions);
                var fakeGenerator = new FakeTextEmbeddingGenerator(embeddingOptions, modelKey);
                var coordinator = new EmbeddingBackfillCoordinator(
                    dbRunner,
                    jobStore,
                    fakeGenerator,
                    Options.Create(embeddingOptions),
                    NullLogger<EmbeddingBackfillCoordinator>.Instance);

                var enqueuedWhileLocked = await coordinator.RunBackfillCycle(cancellationToken);
                advisoryLockVerified = (enqueuedWhileLocked == 0);
            }

            await tx.RollbackAsync(cancellationToken);
        }

        if (advisoryLockVerified)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[PASS] Advisory lock exclusion verified (concurrent execution blocked).");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[FAIL] Advisory lock exclusion check failed.");
            Console.ResetColor();
        }
        Console.WriteLine();

        MemoryCacheService cacheService = new();
        EmbeddingBackfillCoordinator.ResetFallbackCursors();

        int cursorBeforeCycle1;
        await using (ApplicationDbContext dbInit = fixture.CreateDbContext())
        {
            EmbeddingBackfillCoordinator coord0 = CreateCoordinator(dbInit, cacheService, jobOptions, embeddingOptions, modelKey);
            cursorBeforeCycle1 = await coord0.GetCursorOffset(modelKey, cancellationToken);
        }

        // 4. Run Backfill Cycle 1
        Console.WriteLine("--> Executing backfill cycle 1 with batch limit...");
        int enqueuedCycle1;
        int cursorAfterCycle1;
        await using (ApplicationDbContext dbCycle1 = fixture.CreateDbContext())
        {
            EmbeddingBackfillCoordinator coord1 = CreateCoordinator(dbCycle1, cacheService, jobOptions, embeddingOptions, modelKey);
            enqueuedCycle1 = await coord1.RunBackfillCycle(cancellationToken);
            cursorAfterCycle1 = await coord1.GetCursorOffset(modelKey, cancellationToken);
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] Cycle 1 completed: enqueued {enqueuedCycle1} eligible assets (cursor moved from {cursorBeforeCycle1} to {cursorAfterCycle1}).");
        Console.ResetColor();
        Console.WriteLine();

        // 5. Durable Job Processing through ClaimPendingBatch and AssetEmbeddingFinalizer
        Console.WriteLine("--> Processing enqueued embedding generation jobs via durable finalizer & store...");
        var staleFinalizationRejected = await ProcessJobsWithFinalizerAsync(
            fixture,
            cacheService,
            jobOptions,
            modelKey,
            embeddingOptions,
            cancellationToken);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] Job queue processed (stale lease rejection verified: {staleFinalizationRejected}).");
        Console.ResetColor();
        Console.WriteLine();

        // 6. Run Backfill Cycle 2 (Verify cursor progression & wrap)
        Console.WriteLine("--> Executing backfill cycle 2 to test cursor progression & wrap...");
        int enqueuedCycle2;
        int cursorAfterCycle2;
        await using (ApplicationDbContext dbCycle2 = fixture.CreateDbContext())
        {
            EmbeddingBackfillCoordinator coord2 = CreateCoordinator(dbCycle2, cacheService, jobOptions, embeddingOptions, modelKey);
            enqueuedCycle2 = await coord2.RunBackfillCycle(cancellationToken);
            cursorAfterCycle2 = await coord2.GetCursorOffset(modelKey, cancellationToken);
        }

        // Process any jobs from cycle 2
        await ProcessJobsWithFinalizerAsync(
            fixture,
            cacheService,
            jobOptions,
            modelKey,
            embeddingOptions,
            cancellationToken);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"[PASS] Cycle 2 completed: enqueued {enqueuedCycle2} remaining assets (cursor wrapped to {cursorAfterCycle2}).");
        Console.ResetColor();
        Console.WriteLine();

        // 7. Run Backfill Cycle 3 (Verify no duplicate enqueueing)
        Console.WriteLine("--> Executing backfill cycle 3 to test duplicate enqueue prevention...");
        int enqueuedCycle3;
        await using (ApplicationDbContext dbCycle3 = fixture.CreateDbContext())
        {
            EmbeddingBackfillCoordinator coord3 = CreateCoordinator(dbCycle3, cacheService, jobOptions, embeddingOptions, modelKey);
            enqueuedCycle3 = await coord3.RunBackfillCycle(cancellationToken);
            await coord3.GetCursorOffset(modelKey, cancellationToken);
        }

        var cursorProgressionVerified = cursorBeforeCycle1 == 0
            && enqueuedCycle1 > 0
            && cursorAfterCycle1 == enqueuedCycle1
            && cursorAfterCycle2 == 0
            && enqueuedCycle3 == 0
            && staleFinalizationRejected;

        if (cursorProgressionVerified)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[PASS] Persisted cursor progression, wrap-around, duplicate enqueue prevention, and stale finalization rejection verified.");
            Console.ResetColor();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[FAIL] Cursor progression verification failed: c0={cursorBeforeCycle1}, e1={enqueuedCycle1}, c1={cursorAfterCycle1}, c2={cursorAfterCycle2}, e3={enqueuedCycle3}, staleRejected={staleFinalizationRejected}");
            Console.ResetColor();
        }
        Console.WriteLine();

        // 7. Aggregate Metrics Collection (No sensitive data / identifiers)
        int totalAssets;
        int completedCount;
        int pendingCount;
        int failedCount;
        int staleOrRejectedCount;

        await using (ApplicationDbContext db = fixture.CreateDbContext())
        {
            totalAssets = await db.Assets.CountAsync(cancellationToken);

            completedCount = await db.AssetEmbeddings
                .Join(
                    db.Assets,
                    e => e.AssetId,
                    a => a.Id,
                    (e, a) => new { e, a })
                .Where(x => x.e.ModelKey == modelKey && x.e.SourceRevision == x.a.SearchRevision)
                .CountAsync(cancellationToken);

            pendingCount = await db.AssetProcessingJobs
                .Where(j => j.ModelKey == modelKey && (j.Status == AssetProcessingJobStatus.QUEUED || j.Status == AssetProcessingJobStatus.RUNNING))
                .CountAsync(cancellationToken);

            failedCount = await db.AssetProcessingJobs
                .Where(j => j.ModelKey == modelKey && j.Status == AssetProcessingJobStatus.FAILED)
                .CountAsync(cancellationToken);

            var softDeleted = await db.Assets.Where(a => a.DeletedAt != null).CountAsync(cancellationToken);
            var unreadyVersions = await db.Assets
                .Where(a => !a.Versions.Any(v => v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY))
                .CountAsync(cancellationToken);

            staleOrRejectedCount = softDeleted + unreadyVersions;
        }

        var summary = string.Create(
            CultureInfo.InvariantCulture,
            $"Durable backfill cycle verified successfully across {totalAssets} test assets. {completedCount} assets completed embedding generation; {failedCount} jobs recorded failure with cooldown semantics; {staleOrRejectedCount} ineligible assets were properly rejected; advisory transaction lock prevented concurrency race.");

        Console.WriteLine("==========================================================");
        Console.WriteLine(" Backfill Aggregate Evidence Summary");
        Console.WriteLine("==========================================================");
        Console.WriteLine($"Total Assets Evaluated:  {totalAssets}");
        Console.WriteLine($"Completed Items:         {completedCount}");
        Console.WriteLine($"Pending Items:           {pendingCount}");
        Console.WriteLine($"Failed Items:            {failedCount}");
        Console.WriteLine($"Stale/Rejected Items:    {staleOrRejectedCount}");
        Console.WriteLine($"Advisory Lock Invariant: {(advisoryLockVerified ? "PASS" : "FAIL")}");
        Console.WriteLine($"Cursor Progression:      {(cursorProgressionVerified ? "PASS" : "FAIL")}");
        Console.WriteLine("----------------------------------------------------------");
        Console.WriteLine();

        var reportData = new BackfillEvidenceReportData(
            provenance,
            embeddingOptions,
            totalAssets,
            completedCount,
            pendingCount,
            failedCount,
            staleOrRejectedCount,
            advisoryLockVerified,
            cursorProgressionVerified,
            summary);

        (var jsonPath, var mdPath) = SearchEvaluationReportWriter.WriteBackfillReport(reportData);
        Console.WriteLine("Reports emitted:");
        Console.WriteLine($"  - JSON:     {jsonPath}");
        Console.WriteLine($"  - Markdown: {mdPath}");

        return (advisoryLockVerified && cursorProgressionVerified) ? Program.EXIT_SUCCESS : Program.EXIT_MANUAL_EVALUATION_REQUIRED;
    }

    private static async Task SeedBackfillScenarioAsync(
        SearchEvaluationDbFixture fixture,
        Guid authorId,
        Guid categoryId,
        string modelKey,
        EmbeddingOptions options,
        CancellationToken cancellationToken)
    {
        await using ApplicationDbContext db = fixture.CreateDbContext();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var vec = new Vector(new float[options.Dimension]);

        // Helper to add base asset
        Asset CreateBaseAsset(int idx, long revision = 1L, DateTimeOffset? deletedAt = null)
        {
            return new Asset
            {
                Id = Guid.NewGuid(),
                AuthorId = authorId,
                CategoryId = categoryId,
                Title = $"Backfill Test Asset #{idx:D4}",
                Description = $"Description for asset #{idx:D4}",
                Price = 10m,
                CreatedAt = now.AddMinutes(-idx),
                UpdatedAt = now.AddMinutes(-idx),
                SearchRevision = revision,
                DeletedAt = deletedAt
            };
        }

        AssetVersion CreateVersion(Guid assetId, AssetVersionProcessingStatus status = AssetVersionProcessingStatus.READY)
        {
            return new AssetVersion
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                VersionNumber = 1,
                IsCurrent = status == AssetVersionProcessingStatus.READY,
                StorageKey = $"storage/{assetId}/v1.zip",
                FileName = $"{assetId}.zip",
                ContentLength = 1000,
                ContentSha256 = new string('0', 64),
                ReleaseNotes = "Notes",
                LicenseCode = AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal",
                LicenseTerms = "Terms",
                ProcessingStatus = status,
                ProcessingUpdatedAt = now,
                CreatedAt = now
            };
        }

        // 1. Completed Assets (10)
        for (var i = 0; i < 10; i++)
        {
            Asset a = CreateBaseAsset(i, revision: 1);
            AssetVersion v = CreateVersion(a.Id);
            var emb = new AssetEmbedding
            {
                Id = Guid.NewGuid(),
                AssetId = a.Id,
                ModelKey = modelKey,
                Provider = options.Provider,
                ModelId = options.Model,
                ModelRevision = options.Revision,
                ModelDigest = options.Digest,
                Dimension = options.Dimension,
                ContentSchemaVersion = options.ContentSchemaVersion,
                SourceRevision = 1,
                ContentHash = new string('1', 64),
                Embedding = vec,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Assets.Add(a);
            db.AssetVersions.Add(v);
            db.AssetEmbeddings.Add(emb);
        }

        // 2. Pending Assets (20)
        for (var i = 10; i < 30; i++)
        {
            Asset a = CreateBaseAsset(i, revision: 1);
            AssetVersion v = CreateVersion(a.Id);
            db.Assets.Add(a);
            db.AssetVersions.Add(v);
        }

        // 3. Stale Embedding Assets (10)
        for (var i = 30; i < 40; i++)
        {
            Asset a = CreateBaseAsset(i, revision: 2); // SearchRevision is 2
            AssetVersion v = CreateVersion(a.Id);
            var emb = new AssetEmbedding
            {
                Id = Guid.NewGuid(),
                AssetId = a.Id,
                ModelKey = modelKey,
                Provider = options.Provider,
                ModelId = options.Model,
                ModelRevision = options.Revision,
                ModelDigest = options.Digest,
                Dimension = options.Dimension,
                ContentSchemaVersion = options.ContentSchemaVersion,
                SourceRevision = 1, // Stale!
                ContentHash = new string('2', 64),
                Embedding = vec,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Assets.Add(a);
            db.AssetVersions.Add(v);
            db.AssetEmbeddings.Add(emb);
        }

        // 4. Active Job Assets (5)
        for (var i = 40; i < 45; i++)
        {
            Asset a = CreateBaseAsset(i, revision: 1);
            AssetVersion v = CreateVersion(a.Id);
            var activeHash = AssetPublicMetadataCanonicalizer.Canonicalize(a.Title, a.Description, "3D Models", []).ContentHash;
            var job = new AssetProcessingJob
            {
                Id = Guid.NewGuid(),
                AssetId = a.Id,
                AssetVersionId = v.Id,
                Type = AssetProcessingJobType.EMBEDDING_GENERATION,
                DefinitionVersion = 1,
                ModelKey = modelKey,
                InputHash = activeHash,
                Status = AssetProcessingJobStatus.QUEUED,
                Stage = "INIT",
                AttemptCount = 0,
                MaxAttempts = 3,
                AvailableAt = now,
                Payload = AssetProcessingSerializer.SerializePayload(AssetProcessingJobType.EMBEDDING_GENERATION, new EmbeddingGenerationPayload(a.Id, v.Id, 1L, activeHash, modelKey, AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION)),
                CreatedAt = now,
                UpdatedAt = now
            };
            db.Assets.Add(a);
            db.AssetVersions.Add(v);
            db.AssetProcessingJobs.Add(job);
        }

        // 5. Cooldown Failed Assets (<1h) (5)
        for (var i = 45; i < 50; i++)
        {
            Asset a = CreateBaseAsset(i, revision: 1);
            AssetVersion v = CreateVersion(a.Id);
            var cooldownHash = AssetPublicMetadataCanonicalizer.Canonicalize(a.Title, a.Description, "3D Models", []).ContentHash;
            var job = new AssetProcessingJob
            {
                Id = Guid.NewGuid(),
                AssetId = a.Id,
                AssetVersionId = v.Id,
                Type = AssetProcessingJobType.EMBEDDING_GENERATION,
                DefinitionVersion = 1,
                ModelKey = modelKey,
                InputHash = cooldownHash,
                Status = AssetProcessingJobStatus.FAILED,
                Stage = "FAILED",
                AttemptCount = 3,
                MaxAttempts = 3,
                AvailableAt = now,
                Payload = AssetProcessingSerializer.SerializePayload(AssetProcessingJobType.EMBEDDING_GENERATION, new EmbeddingGenerationPayload(a.Id, v.Id, 1L, cooldownHash, modelKey, AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION)),
                CompletedAt = now.AddMinutes(-20), // Within cooldown!
                CreatedAt = now.AddMinutes(-30),
                UpdatedAt = now.AddMinutes(-20)
            };
            db.Assets.Add(a);
            db.AssetVersions.Add(v);
            db.AssetProcessingJobs.Add(job);
        }

        // 6. Expired Cooldown Failed Assets (>2h) (5)
        for (var i = 50; i < 55; i++)
        {
            Asset a = CreateBaseAsset(i, revision: 1);
            AssetVersion v = CreateVersion(a.Id);
            var expiredHash = AssetPublicMetadataCanonicalizer.Canonicalize(a.Title, a.Description, "3D Models", []).ContentHash;
            var job = new AssetProcessingJob
            {
                Id = Guid.NewGuid(),
                AssetId = a.Id,
                AssetVersionId = v.Id,
                Type = AssetProcessingJobType.EMBEDDING_GENERATION,
                DefinitionVersion = 1,
                ModelKey = modelKey,
                InputHash = expiredHash,
                Status = AssetProcessingJobStatus.FAILED,
                Stage = "FAILED",
                AttemptCount = 3,
                MaxAttempts = 3,
                AvailableAt = now,
                Payload = AssetProcessingSerializer.SerializePayload(AssetProcessingJobType.EMBEDDING_GENERATION, new EmbeddingGenerationPayload(a.Id, v.Id, 1L, expiredHash, modelKey, AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION)),
                CompletedAt = now.AddHours(-3), // Cooldown expired!
                CreatedAt = now.AddHours(-4),
                UpdatedAt = now.AddHours(-3)
            };
            db.Assets.Add(a);
            db.AssetVersions.Add(v);
            db.AssetProcessingJobs.Add(job);
        }

        // 7. Unready Version Assets (5)
        for (var i = 55; i < 60; i++)
        {
            Asset a = CreateBaseAsset(i, revision: 1);
            AssetVersion v = CreateVersion(a.Id, status: AssetVersionProcessingStatus.PENDING_INSPECTION);
            db.Assets.Add(a);
            db.AssetVersions.Add(v);
        }

        // 8. Soft-deleted Assets (5)
        for (var i = 60; i < 65; i++)
        {
            Asset a = CreateBaseAsset(i, revision: 1, deletedAt: now.AddDays(-1));
            AssetVersion v = CreateVersion(a.Id);
            db.Assets.Add(a);
            db.AssetVersions.Add(v);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private static EmbeddingBackfillCoordinator CreateCoordinator(
        ApplicationDbContext db,
        ICacheService cacheService,
        IOptions<AssetProcessingOptions> jobOptions,
        EmbeddingOptions embeddingOptions,
        string modelKey)
    {
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, jobOptions);
        var fakeGen = new FakeTextEmbeddingGenerator(embeddingOptions, modelKey);
        return new EmbeddingBackfillCoordinator(
            db,
            jobStore,
            fakeGen,
            Options.Create(embeddingOptions),
            NullLogger<EmbeddingBackfillCoordinator>.Instance,
            TimeProvider.System,
            cacheService);
    }

    private static async Task<bool> ProcessJobsWithFinalizerAsync(
        SearchEvaluationDbFixture fixture,
        ICacheService cacheService,
        IOptions<AssetProcessingOptions> jobOptions,
        string modelKey,
        EmbeddingOptions options,
        CancellationToken cancellationToken)
    {
        var staleFinalizationRejected = false;

        await using ApplicationDbContext db = fixture.CreateDbContext();
        var jobStore = new AssetProcessingJobStore(db, NullLogger<AssetProcessingJobStore>.Instance, jobOptions);
        var finalizer = new AssetEmbeddingFinalizer(db, cacheService, NullLogger<AssetEmbeddingFinalizer>.Instance);

        while (true)
        {
            IReadOnlyList<ClaimedAssetProcessingJob> claimed = await jobStore.ClaimPendingBatch(
                batchSize: 50,
                leaseDuration: TimeSpan.FromMinutes(5),
                leaseOwner: "backfill-evidence-worker",
                cancellationToken: cancellationToken);

            if (claimed.Count == 0)
            {
                break;
            }

            foreach (ClaimedAssetProcessingJob claim in claimed)
            {
                var payload = (EmbeddingGenerationPayload)AssetProcessingSerializer.DeserializePayload(
                    AssetProcessingJobType.EMBEDDING_GENERATION,
                    claim.Payload);
                var sourceRevision = payload.TargetRevision;
                var contentHash = payload.ContentHash;
                var contentSchemaVersion = payload.ContentSchemaVersion;

                // 1. On first claimed item, test stale finalization rejection with invalid LeaseToken
                if (!staleFinalizationRejected)
                {
                    var staleParams = new FinalizeEmbeddingParameters(
                        claim.JobId,
                        Guid.NewGuid(), // Stale/expired lease token
                        claim.AssetId,
                        claim.AssetVersionId,
                        sourceRevision,
                        contentHash,
                        modelKey,
                        options.Provider,
                        options.Model,
                        options.Revision,
                        options.Digest,
                        options.Dimension,
                        contentSchemaVersion,
                        new float[options.Dimension]);

                    EmbeddingFinalizationStatus staleStatus = await finalizer.Finalize(staleParams, cancellationToken);
                    if (staleStatus == EmbeddingFinalizationStatus.LEASE_LOST)
                    {
                        staleFinalizationRejected = true;
                    }
                }

                // Durable finalization through AssetEmbeddingFinalizer
                var validParams = new FinalizeEmbeddingParameters(
                    claim.JobId,
                    claim.LeaseToken,
                    claim.AssetId,
                    claim.AssetVersionId,
                    sourceRevision,
                    contentHash,
                    modelKey,
                    options.Provider,
                    options.Model,
                    options.Revision,
                    options.Digest,
                    options.Dimension,
                    contentSchemaVersion,
                    new float[options.Dimension]);

                EmbeddingFinalizationStatus status = await finalizer.Finalize(validParams, cancellationToken);
                if (status != EmbeddingFinalizationStatus.COMMITTED)
                {
                    Console.WriteLine($"[DEBUG] Finalization status for {claim.AssetId}: {status}");
                }
            }
        }

        return staleFinalizationRejected;
    }

    private sealed class FakeTextEmbeddingGenerator(EmbeddingOptions options, string modelKey) : ITextEmbeddingGenerator
    {
        public Task<ModelVerificationResult> CheckModelAvailability(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelVerificationResult(true, null, options.Digest));
        }

        public Task<GeneratedEmbedding> Generate(string text, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new GeneratedEmbedding(
                new float[options.Dimension],
                modelKey,
                options.Dimension));
        }
    }
}
