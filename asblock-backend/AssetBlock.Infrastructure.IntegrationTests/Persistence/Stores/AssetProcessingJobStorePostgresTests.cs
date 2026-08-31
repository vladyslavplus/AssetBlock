using System.Text.Json;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// PostgreSQL-only store tests: claim locking, lease fencing against the database clock,
/// idempotent enqueue, recovery, and JSON constraints. SQLite/InMemory cannot prove any of this.
/// Uses CreateCleanDbContext to verify against real applied EF migrations.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class AssetProcessingJobStorePostgresTests(PostgresFixture fixture)
{
    private static readonly TimeSpan _leaseDuration = TimeSpan.FromMinutes(1);

    private static AssetProcessingJobStore CreateStore(ApplicationDbContext db) =>
        new(db, NullLogger<AssetProcessingJobStore>.Instance, Microsoft.Extensions.Options.Options.Create(new AssetProcessingOptions()));

    private async Task<SeedData> Seed(int jobCount = 1, AssetProcessingJobType type = AssetProcessingJobType.ARCHIVE_INSPECTION)
    {
        ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        AssetProcessingJobStore store = CreateStore(db);
        var jobIds = new List<Guid>(jobCount);
        AssetProcessingPayload payload = type switch
        {
            AssetProcessingJobType.MALWARE_SCAN => new MalwareScanPayload("1.0"),
            AssetProcessingJobType.LISTING_COPILOT => new ListingCopilotPayload("copilot-policy-1"),
            _ => new ArchiveInspectionPayload()
        };

        for (var i = 0; i < jobCount; i++)
        {
            jobIds.Add(await store.Enqueue(
                asset.Id,
                version.Id,
                type,
                definitionVersion: 1 + i,
                TimeSpan.Zero,
                payload));
        }

        return new SeedData(db, author, asset, version, store, jobIds);
    }

    private sealed record SeedData(
        ApplicationDbContext Db,
        User Author,
        Asset Asset,
        AssetVersion Version,
        AssetProcessingJobStore Store,
        List<Guid> JobIds);

    [Fact]
    public async Task ClaimPendingBatch_WhenCalledConcurrently_ShouldClaimEachJobExactlyOnce()
    {
        const int jobCount = 6;
        const int workerCount = 8;
        SeedData seed = await Seed(jobCount);

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workers = Enumerable.Range(0, workerCount)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                await using ApplicationDbContext db = fixture.CreateDbContext();
                return await CreateStore(db).ClaimPendingBatch(jobCount, _leaseDuration, $"worker-{Guid.NewGuid():N}");
            }))
            .ToList();

        start.SetResult();
        IReadOnlyList<ClaimedAssetProcessingJob>[] batches = await Task.WhenAll(workers);

        var claimed = batches.SelectMany(b => b).ToList();
        claimed.Should().HaveCount(jobCount);
        claimed.Select(j => j.JobId).Should().OnlyHaveUniqueItems();
        claimed.Should().AllSatisfy(j =>
        {
            j.OwnerUserId.Should().Be(seed.Author.Id);
            j.LeaseToken.Should().NotBeEmpty();
            j.AttemptCount.Should().Be(1);
        });

        seed.Db.ChangeTracker.Clear();
        List<AssetProcessingJob> stored = await seed.Db.AssetProcessingJobs.AsNoTracking().ToListAsync();
        stored.Should().OnlyContain(j => j.Status == AssetProcessingJobStatus.RUNNING);
        stored.Count(j => j.LeaseToken != null).Should().Be(jobCount);
    }

    [Fact]
    public async Task ClaimPendingBatch_WhenRowIsLocked_ShouldSkipLockedRowAndClaimRemaining()
    {
        SeedData seed = await Seed(jobCount: 2);
        Guid blockedId = seed.JobIds[0];

        await using (ApplicationDbContext blockerDb = fixture.CreateDbContext())
        await using (await blockerDb.Database.BeginTransactionAsync())
        {
            await blockerDb.Database.ExecuteSqlRawAsync(
                """SELECT "Id" FROM asset_processing_jobs WHERE "Id" = {0} FOR UPDATE""", blockedId);

            await using ApplicationDbContext claimDb = fixture.CreateDbContext();
            IReadOnlyList<ClaimedAssetProcessingJob> first = await CreateStore(claimDb).ClaimPendingBatch(10, _leaseDuration, "worker-a");

            first.Should().ContainSingle(j => j.JobId == seed.JobIds[1]);
            first.Should().NotContain(j => j.JobId == blockedId);
        }

        // After the lock holder releases, the skipped row becomes claimable again.
        await using ApplicationDbContext nextDb = fixture.CreateDbContext();
        IReadOnlyList<ClaimedAssetProcessingJob> second = await CreateStore(nextDb).ClaimPendingBatch(10, _leaseDuration, "worker-b");
        second.Should().ContainSingle(j => j.JobId == blockedId);
    }

    [Fact]
    public async Task Enqueue_WhenDuplicateKeyExists_ShouldReturnExistingJobId()
    {
        SeedData seed = await Seed();
        Guid jobId = seed.JobIds[0];

        Guid duplicateId = await seed.Store.Enqueue(
            seed.Asset.Id,
            seed.Version.Id,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            definitionVersion: 1,
            TimeSpan.Zero,
            new ArchiveInspectionPayload());

        duplicateId.Should().Be(jobId);
        (await seed.Db.AssetProcessingJobs.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Enqueue_WhenCalledConcurrently_ShouldCreateExactlyOneJob()
    {
        const int CALLERS = 6;
        SeedData seed = await Seed();

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callers = Enumerable.Range(0, CALLERS)
            .Select(_ => Task.Run(async () =>
            {
                await start.Task;
                await using ApplicationDbContext db = fixture.CreateDbContext();
                return await CreateStore(db).Enqueue(
                    seed.Asset.Id,
                    seed.Version.Id,
                    AssetProcessingJobType.MALWARE_SCAN,
                    definitionVersion: 1,
                    TimeSpan.Zero,
                    new MalwareScanPayload("policy-v1"));
            }))
            .ToList();

        start.SetResult();
        Guid[] ids = await Task.WhenAll(callers);

        ids.Distinct().Should().ContainSingle("all concurrent enqueues must converge on one row");
        (await seed.Db.AssetProcessingJobs.AsNoTracking().CountAsync(j => j.Type == AssetProcessingJobType.MALWARE_SCAN)).Should().Be(1);
    }

    [Fact]
    public async Task RenewLease_WhenLeaseValid_ShouldExtend_WhenLeaseExpired_ShouldReturnFalse()
    {
        SeedData seed = await Seed();
        (Guid jobId, Guid leaseToken) = await Claim(seed.Store, seed.JobIds[0]);

        DateTimeOffset? before = await GetLeaseExpiry(seed.Db, jobId);
        (await seed.Store.RenewLease(jobId, leaseToken, _leaseDuration)).Should().BeTrue();
        DateTimeOffset? after = await GetLeaseExpiry(seed.Db, jobId);
        (after!.Value - before!.Value).Should().BePositive();

        ExpireLease(seed.Db, jobId);
        (await seed.Store.RenewLease(jobId, leaseToken, _leaseDuration)).Should().BeFalse();
    }

    [Theory]
    [InlineData(AssetProcessingJobStatus.SUCCEEDED)]
    [InlineData(AssetProcessingJobStatus.FAILED)]
    [InlineData(AssetProcessingJobStatus.CANCELLED)]
    public async Task TerminalMarks_WhenWorkerLeaseExpiredBeforeUpdate_ShouldReturnFalseAndKeepRunningState(AssetProcessingJobStatus target)
    {
        SeedData seed = await Seed();
        (Guid jobId, Guid leaseToken) = await Claim(seed.Store, seed.JobIds[0]);

        ExpireLease(seed.Db, jobId);

        var updated = target switch
        {
            AssetProcessingJobStatus.SUCCEEDED => await seed.Store.MarkSucceeded(jobId, leaseToken, new ArchiveInspectionResult(1, 10)),
            AssetProcessingJobStatus.FAILED => await seed.Store.MarkFailedTerminal(jobId, leaseToken, "SCAN_UNAVAILABLE", "Scanner unreachable"),
            _ => await seed.Store.MarkCancelled(jobId, leaseToken)
        };

        updated.Should().BeFalse();

        seed.Db.ChangeTracker.Clear();
        AssetProcessingJob job = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.Status.Should().Be(AssetProcessingJobStatus.RUNNING, "an expired lease must never produce a terminal outcome");
        job.CompletedAt.Should().BeNull();
        job.Result.Should().BeNull();
        job.LeaseToken.Should().Be(leaseToken);
    }

    [Fact]
    public async Task MarkFailedRetryable_WhenAttemptsRemain_ShouldScheduleRetry_WhenExhausted_ShouldFailTerminally()
    {
        SeedData seed = await Seed();
        (Guid jobId, Guid leaseToken) = await Claim(seed.Store, seed.JobIds[0]);
        var retryDelay = TimeSpan.FromMinutes(2);

        (await seed.Store.MarkFailedRetryable(jobId, leaseToken, "SCAN_TIMEOUT", "Scan timed out", retryDelay)).Should().BeTrue();

        seed.Db.ChangeTracker.Clear();
        AssetProcessingJob job = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.Status.Should().Be(AssetProcessingJobStatus.RETRY_SCHEDULED);
        job.AvailableAt.Should().BeCloseTo(DateTimeOffset.UtcNow + retryDelay, precision: TimeSpan.FromSeconds(5));
        job.CompletedAt.Should().BeNull();
        job.ErrorCode.Should().Be("SCAN_TIMEOUT");
        job.LeaseOwner.Should().BeNull();

        // Exhaust attempts on the retry: cap MaxAttempts at current count, then make it claimable.
        SetMaxAttempts(seed.Db, jobId, maxAttempts: 2);
        MakeAvailableNow(seed.Db, jobId);
        await using ApplicationDbContext retryDb = fixture.CreateDbContext();
        AssetProcessingJobStore retryStore = CreateStore(retryDb);
        (Guid claimedId, Guid retryToken) = await Claim(retryStore, jobId);
        claimedId.Should().Be(jobId);
        (await retryStore.MarkFailedRetryable(jobId, retryToken, "SCAN_TIMEOUT", "Scan timed out again", retryDelay)).Should().BeTrue();

        seed.Db.ChangeTracker.Clear();
        AssetProcessingJob exhausted = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        exhausted.Status.Should().Be(AssetProcessingJobStatus.FAILED);
        exhausted.Stage.Should().Be("FAILED_ATTEMPTS_EXHAUSTED");
        exhausted.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task MarkFailedTerminal_WithInvalidErrorCode_ThrowsArgumentException()
    {
        SeedData seed = await Seed();
        (Guid _, Guid leaseToken) = await Claim(seed.Store, seed.JobIds[0]);

        Func<Task<bool>> act = () => seed.Store.MarkFailedTerminal(seed.JobIds[0], leaseToken, "bad-code!", "boom");
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RecoverExpiredLeases_WhenAttemptsRemain_ShouldRequeueOnlyExpiredJobs()
    {
        SeedData seed = await Seed(jobCount: 2);
        (Guid expiredJobId, Guid _) = await Claim(seed.Store, seed.JobIds[0]);
        (Guid liveJobId, Guid _) = await Claim(seed.Store, seed.JobIds[1]);

        ExpireLease(seed.Db, expiredJobId);

        var recovered = await seed.Store.RecoverExpiredLeases();
        recovered.Should().Be(1);

        seed.Db.ChangeTracker.Clear();
        AssetProcessingJob expired = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == expiredJobId);
        expired.Status.Should().Be(AssetProcessingJobStatus.RETRY_SCHEDULED);
        expired.Stage.Should().Be("LEASE_RECOVERED");
        expired.ErrorCode.Should().Be("LEASE_EXPIRED");
        expired.AvailableAt.Should().BeOnOrBefore(DateTimeOffset.UtcNow.AddSeconds(1));
        expired.CompletedAt.Should().BeNull();

        AssetProcessingJob live = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == liveJobId);
        live.Status.Should().Be(AssetProcessingJobStatus.RUNNING);
    }

    [Fact]
    public async Task RecoverExpiredLeases_WhenAttemptsExhausted_ShouldFailJobTerminally()
    {
        SeedData seed = await Seed(type: AssetProcessingJobType.LISTING_COPILOT);
        (Guid jobId, Guid _) = await Claim(seed.Store, seed.JobIds[0]);

        SetAttemptCount(seed.Db, jobId, maxAttempts: 1, attemptCount: 1);
        ExpireLease(seed.Db, jobId);

        (await seed.Store.RecoverExpiredLeases()).Should().Be(1);

        seed.Db.ChangeTracker.Clear();
        AssetProcessingJob job = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.Status.Should().Be(AssetProcessingJobStatus.FAILED);
        job.Stage.Should().Be("FAILED_LEASE_EXPIRED");
        job.CompletedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RecoverExpiredLeases_WhenExhaustedSecurityJob_ShouldLeaveJobForLifecycleRecovery()
    {
        SeedData seed = await Seed();
        (Guid jobId, Guid _) = await Claim(seed.Store, seed.JobIds[0]);

        SetAttemptCount(seed.Db, jobId, maxAttempts: 1, attemptCount: 1);
        ExpireLease(seed.Db, jobId);

        (await seed.Store.RecoverExpiredLeases()).Should().Be(0);

        seed.Db.ChangeTracker.Clear();
        AssetProcessingJob job = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.Status.Should().Be(AssetProcessingJobStatus.RUNNING);
        AssetVersion version = await seed.Db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id);
        version.ProcessingStatus.Should().NotBe(AssetVersionProcessingStatus.PROCESSING_FAILED);
    }

    [Fact]
    public async Task MarkSucceeded_ForEveryJobType_ShouldStoreTypedJsonResultOnce()
    {
        ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        AssetProcessingJobStore store = CreateStore(db);
        var cases = new (AssetProcessingJobType Type, AssetProcessingPayload Payload, AssetProcessingResult Result)[]
        {
            (AssetProcessingJobType.ARCHIVE_INSPECTION, new ArchiveInspectionPayload(), new ArchiveInspectionResult(3, 4096)),
            (AssetProcessingJobType.MALWARE_SCAN, new MalwareScanPayload("clamav-policy-1"), new MalwareScanResult(true)),
            (AssetProcessingJobType.LISTING_COPILOT, new ListingCopilotPayload("copilot-policy-1"), new ListingCopilotResult(true, new string('a', 64)))
        };

        foreach ((AssetProcessingJobType type, AssetProcessingPayload? payload, AssetProcessingResult? result) in cases)
        {
            Guid jobId = await store.Enqueue(asset.Id, version.Id, type, definitionVersion: 1, TimeSpan.Zero, payload);
            await using ApplicationDbContext claimDb = fixture.CreateDbContext();
            IReadOnlyList<ClaimedAssetProcessingJob> batch = await CreateStore(claimDb).ClaimPendingBatch(1, _leaseDuration, $"worker-{type}");
            ClaimedAssetProcessingJob claimed = batch.Should().ContainSingle(j => j.JobId == jobId).Subject;
            claimed.Type.Should().Be(type);

            (await store.MarkSucceeded(jobId, claimed.LeaseToken, result)).Should().BeTrue();
        }

        db.ChangeTracker.Clear();
        List<AssetProcessingJob> jobs = await db.AssetProcessingJobs.AsNoTracking().ToListAsync();
        jobs.Should().OnlyContain(j => j.Status == AssetProcessingJobStatus.SUCCEEDED && j.CompletedAt != null && j.LeaseToken == null);

        JsonElement inspection = JsonDocument.Parse(jobs.Single(j => j.Type == AssetProcessingJobType.ARCHIVE_INSPECTION).Result!).RootElement;
        inspection.GetProperty("fileCount").GetInt32().Should().Be(3);
        inspection.GetProperty("totalSizeUncompressed").GetInt64().Should().Be(4096);

        JsonElement scan = JsonDocument.Parse(jobs.Single(j => j.Type == AssetProcessingJobType.MALWARE_SCAN).Result!).RootElement;
        scan.GetProperty("isClean").GetBoolean().Should().BeTrue();

        JsonElement copilot = JsonDocument.Parse(jobs.Single(j => j.Type == AssetProcessingJobType.LISTING_COPILOT).Result!).RootElement;
        copilot.GetProperty("success").GetBoolean().Should().BeTrue();
        copilot.GetProperty("contentHash").GetString().Should().Be(new string('a', 64));
    }

    [Fact]
    public async Task MarkSucceeded_WhenJobMissingOrWrongLease_ShouldReturnFalse()
    {
        SeedData seed = await Seed();
        (Guid jobId, Guid leaseToken) = await Claim(seed.Store, seed.JobIds[0]);

        (await seed.Store.MarkSucceeded(Guid.NewGuid(), leaseToken, new ArchiveInspectionResult(1, 1))).Should().BeFalse();
        (await seed.Store.MarkSucceeded(jobId, Guid.NewGuid(), new ArchiveInspectionResult(1, 1))).Should().BeFalse();

        seed.Db.ChangeTracker.Clear();
        AssetProcessingJob job = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        job.Status.Should().Be(AssetProcessingJobStatus.RUNNING);
        job.Result.Should().BeNull();
    }

    [Fact]
    public async Task MarkSucceeded_WhenLeaseExpiresDuringLockWait_ShouldReturnFalseAndNotMutateJob()
    {
        SeedData seed = await Seed();
        IReadOnlyList<ClaimedAssetProcessingJob> batch = await seed.Store.ClaimPendingBatch(1, TimeSpan.FromSeconds(1), "worker-test");
        ClaimedAssetProcessingJob claimed = batch.Should().ContainSingle(j => j.JobId == seed.JobIds[0]).Subject;
        Guid jobId = claimed.JobId;
        Guid leaseToken = claimed.LeaseToken;

        await using ApplicationDbContext blockerDb = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTx = await blockerDb.Database.BeginTransactionAsync();

        await blockerDb.Database.ExecuteSqlRawAsync(
            """SELECT "Id" FROM asset_processing_jobs WHERE "Id" = {0} FOR UPDATE""", jobId);

        await using ApplicationDbContext workerDb = fixture.CreateDbContext();
        var workerConn = (NpgsqlConnection)workerDb.Database.GetDbConnection();
        await workerConn.OpenAsync();
        var workerPid = workerConn.ProcessID;

        Task<bool> markTask = Task.Run(async () =>
        {
            AssetProcessingJobStore workerStore = CreateStore(workerDb);
            return await workerStore.MarkSucceeded(jobId, leaseToken, new ArchiveInspectionResult(5, 1024));
        });

        await using ApplicationDbContext monitorDb = fixture.CreateDbContext();
        await WaitForLockWait(monitorDb, workerPid, TimeSpan.FromSeconds(5));

        await Task.Delay(1100);

        await blockerTx.RollbackAsync();

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var succeeded = await markTask.WaitAsync(testCts.Token);
        succeeded.Should().BeFalse("fencing check after acquiring lock must detect that lease expired during wait");

        seed.Db.ChangeTracker.Clear();
        AssetProcessingJob job = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == jobId, cancellationToken: testCts.Token);
        job.Status.Should().Be(AssetProcessingJobStatus.RUNNING, "expired worker must not change job to terminal status");
        job.CompletedAt.Should().BeNull();
        job.Result.Should().BeNull();
    }

    [Fact]
    public async Task RenewLease_WhenLeaseExpiresDuringLockWait_ShouldReturnFalseAndNotExtend()
    {
        SeedData seed = await Seed();
        IReadOnlyList<ClaimedAssetProcessingJob> batch = await seed.Store.ClaimPendingBatch(1, TimeSpan.FromSeconds(1), "worker-test");
        ClaimedAssetProcessingJob claimed = batch.Should().ContainSingle(j => j.JobId == seed.JobIds[0]).Subject;
        Guid jobId = claimed.JobId;
        Guid leaseToken = claimed.LeaseToken;

        await using ApplicationDbContext blockerDb = fixture.CreateDbContext();
        await using IDbContextTransaction blockerTx = await blockerDb.Database.BeginTransactionAsync();

        await blockerDb.Database.ExecuteSqlRawAsync(
            """SELECT "Id" FROM asset_processing_jobs WHERE "Id" = {0} FOR UPDATE""", jobId);

        await using ApplicationDbContext workerDb = fixture.CreateDbContext();
        var workerConn = (NpgsqlConnection)workerDb.Database.GetDbConnection();
        await workerConn.OpenAsync();
        var workerPid = workerConn.ProcessID;

        Task<bool> renewTask = Task.Run(async () =>
        {
            AssetProcessingJobStore workerStore = CreateStore(workerDb);
            return await workerStore.RenewLease(jobId, leaseToken, TimeSpan.FromMinutes(5));
        });

        await using ApplicationDbContext monitorDb = fixture.CreateDbContext();
        await WaitForLockWait(monitorDb, workerPid, TimeSpan.FromSeconds(5));

        await Task.Delay(1100);

        await blockerTx.RollbackAsync();

        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var renewed = await renewTask.WaitAsync(testCts.Token);
        renewed.Should().BeFalse("renew after lock wait must detect that lease already expired");
    }

    private static async Task WaitForLockWait(ApplicationDbContext db, int workerPid, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync(cts.Token);
        }

        while (!cts.IsCancellationRequested)
        {
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM pg_stat_activity
                    WHERE pid = @pid
                      AND (wait_event_type = 'Lock' OR cardinality(pg_blocking_pids(pid)) > 0)
                );
                """;
            cmd.Parameters.AddWithValue("pid", workerPid);

            var result = await cmd.ExecuteScalarAsync(cts.Token);
            if (result is true)
            {
                return;
            }

            await Task.Delay(20, cts.Token);
        }

        throw new TimeoutException($"Worker process PID {workerPid} did not enter lock wait within {timeout.TotalSeconds}s.");
    }

    [Fact]
    public async Task MarkCancelled_WhenJobHadPriorRetryError_ShouldClearErrorCodeAndErrorSummary()
    {
        SeedData seed = await Seed();
        (Guid jobId, Guid firstLeaseToken) = await Claim(seed.Store, seed.JobIds[0]);

        var retryDelay = TimeSpan.FromMinutes(1);
        (await seed.Store.MarkFailedRetryable(jobId, firstLeaseToken, "SCAN_TIMEOUT", "Scanner timed out on attempt 1", retryDelay)).Should().BeTrue();

        seed.Db.ChangeTracker.Clear();
        AssetProcessingJob retryJob = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        retryJob.Status.Should().Be(AssetProcessingJobStatus.RETRY_SCHEDULED);
        retryJob.ErrorCode.Should().Be("SCAN_TIMEOUT");
        retryJob.ErrorSummary.Should().Be("Scanner timed out on attempt 1");

        MakeAvailableNow(seed.Db, jobId);
        await using ApplicationDbContext claimDb = fixture.CreateDbContext();
        AssetProcessingJobStore secondStore = CreateStore(claimDb);
        (Guid claimedId, Guid secondLeaseToken) = await Claim(secondStore, jobId);
        claimedId.Should().Be(jobId);

        (await secondStore.MarkCancelled(jobId, secondLeaseToken)).Should().BeTrue();

        seed.Db.ChangeTracker.Clear();
        AssetProcessingJob cancelledJob = await seed.Db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        cancelledJob.Status.Should().Be(AssetProcessingJobStatus.CANCELLED);
        cancelledJob.Stage.Should().Be("CANCELLED");
        cancelledJob.CompletedAt.Should().NotBeNull();
        cancelledJob.ErrorCode.Should().BeNull("cancellation must clear stale retry/recovery error code");
        cancelledJob.ErrorSummary.Should().BeNull("cancellation must clear stale retry/recovery error summary");
        cancelledJob.LeaseOwner.Should().BeNull();
        cancelledJob.LeaseToken.Should().BeNull();
        cancelledJob.LeaseExpiresAt.Should().BeNull();
    }

    [Fact]
    public async Task GetJobsForAsset_WhenOwner_ReturnsJobs_WhenForeignOrNotFound_ReturnsNull()
    {
        SeedData seed = await Seed(jobCount: 2);
        var foreignUserId = Guid.NewGuid();

        // 1. Owner query returns list
        IReadOnlyList<AssetProcessingJobDto>? ownerJobs = await seed.Store.GetJobsForAsset(seed.Asset.Id, seed.Author.Id);
        ownerJobs.Should().NotBeNull();
        ownerJobs.Should().HaveCount(2);

        // 2. Foreign user query returns null
        IReadOnlyList<AssetProcessingJobDto>? foreignJobs = await seed.Store.GetJobsForAsset(seed.Asset.Id, foreignUserId);
        foreignJobs.Should().BeNull();

        // 3. Nonexistent asset returns null
        IReadOnlyList<AssetProcessingJobDto>? notFoundJobs = await seed.Store.GetJobsForAsset(Guid.NewGuid(), seed.Author.Id);
        notFoundJobs.Should().BeNull();
    }

    [Fact]
    public async Task GetJobsForVersion_WhenOwner_ReturnsJobs_WhenForeignOrNotFound_ReturnsNull()
    {
        SeedData seed = await Seed(jobCount: 2);
        var foreignUserId = Guid.NewGuid();

        // 1. Owner query returns list
        IReadOnlyList<AssetProcessingJobDto>? ownerJobs = await seed.Store.GetJobsForVersion(seed.Version.Id, seed.Author.Id);
        ownerJobs.Should().NotBeNull();
        ownerJobs.Should().HaveCount(2);

        // 2. Foreign user query returns null
        IReadOnlyList<AssetProcessingJobDto>? foreignJobs = await seed.Store.GetJobsForVersion(seed.Version.Id, foreignUserId);
        foreignJobs.Should().BeNull();

        // 3. Nonexistent version returns null
        IReadOnlyList<AssetProcessingJobDto>? notFoundJobs = await seed.Store.GetJobsForVersion(Guid.NewGuid(), seed.Author.Id);
        notFoundJobs.Should().BeNull();
    }

    [Fact]
    public async Task GetRealtimeState_WhenJobExists_ReturnsProjectedStateWithOwnerId()
    {
        SeedData seed = await Seed(jobCount: 1);
        Guid jobId = seed.JobIds[0];

        AssetProcessingJobRealtimeState? state = await seed.Store.GetRealtimeState(jobId);
        state.Should().NotBeNull();
        state.JobId.Should().Be(jobId);
        state.AssetId.Should().Be(seed.Asset.Id);
        state.AssetVersionId.Should().Be(seed.Version.Id);
        state.OwnerUserId.Should().Be(seed.Author.Id);
        state.Status.Should().Be(AssetProcessingJobStatus.QUEUED);
        state.Stage.Should().Be("QUEUED");

        AssetProcessingUpdateMessage clientMsg = state.ToClientMessage();
        clientMsg.JobId.Should().Be(jobId);
        clientMsg.AssetId.Should().Be(seed.Asset.Id);
        clientMsg.AssetVersionId.Should().Be(seed.Version.Id);
    }

    [Fact]
    public async Task TypeColumn_WhenInvalidType_ShouldRejectWithCheckConstraint()
    {
        SeedData seed = await Seed();

        Func<Task<int>> act = () => seed.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "Type" = 'INVALID_TYPE' WHERE "Id" = {0}""", seed.JobIds[0]);

        PostgresException ex = (await act.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(CHECK_VIOLATION_SQLSTATE);
        ex.ConstraintName.Should().Be("CK_asset_processing_jobs_type");
    }

    [Fact]
    public async Task StatusColumn_WhenInvalidStatus_ShouldRejectWithCheckConstraint()
    {
        SeedData seed = await Seed();

        Func<Task<int>> act = () => seed.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "Status" = 'INVALID_STATUS' WHERE "Id" = {0}""", seed.JobIds[0]);

        PostgresException ex = (await act.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(CHECK_VIOLATION_SQLSTATE);
        ex.ConstraintName.Should().Be("CK_asset_processing_jobs_status");
    }

    [Fact]
    public async Task ErrorCodeColumn_WhenInvalidFormat_ShouldRejectWithCheckConstraint()
    {
        SeedData seed = await Seed();

        Func<Task<int>> act = () => seed.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "ErrorCode" = 'invalid-lowercase!' WHERE "Id" = {0}""", seed.JobIds[0]);

        PostgresException ex = (await act.Should().ThrowAsync<PostgresException>()).Which;
        ex.SqlState.Should().Be(CHECK_VIOLATION_SQLSTATE);
        ex.ConstraintName.Should().Be("CK_asset_processing_jobs_error_code");
    }

    [Fact]
    public async Task ErrorCodeColumn_WhenValidFormat_ShouldAccept()
    {
        SeedData seed = await Seed();

        var rows = await seed.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "ErrorCode" = 'VALID_CODE_123' WHERE "Id" = {0}""", seed.JobIds[0]);

        rows.Should().Be(1);
    }

    private const string CHECK_VIOLATION_SQLSTATE = "23514";

    [Fact]
    public async Task PayloadColumn_WhenNonObjectJson_ShouldRejectWithCheckConstraint()
    {
        SeedData seed = await Seed();

        Func<Task<int>> act = () => seed.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "Payload" = '[1,2]'::jsonb WHERE "Id" = {0}""", seed.JobIds[0]);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(CHECK_VIOLATION_SQLSTATE);
    }

    [Fact]
    public async Task PayloadColumn_WhenOver4000Bytes_ShouldRejectWithCheckConstraint()
    {
        SeedData seed = await Seed();
        var oversize = "{\"padding\":\"" + new string('x', 5000) + "\"}";

        Func<Task<int>> act = () => seed.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "Payload" = {1}::jsonb WHERE "Id" = {0}""",
            [seed.JobIds[0], oversize]);

        (await act.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(CHECK_VIOLATION_SQLSTATE);
    }

    [Fact]
    public async Task ResultColumn_WhenScalarJsonOrOversize_ShouldRejectWithCheckConstraint()
    {
        SeedData seed = await Seed();
        (Guid jobId, Guid leaseToken) = await Claim(seed.Store, seed.JobIds[0]);
        (await seed.Store.MarkSucceeded(jobId, leaseToken, new ArchiveInspectionResult(1, 1))).Should().BeTrue();
        seed.Db.ChangeTracker.Clear();

        Func<Task<int>> nonObjectAct = () => seed.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "Result" = '"scalar"'::jsonb WHERE "Id" = {0}""", jobId);
        (await nonObjectAct.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(CHECK_VIOLATION_SQLSTATE);

        var oversize = "{\"padding\":\"" + new string('x', 5000) + "\"}";
        Func<Task<int>> oversizeAct = () => seed.Db.Database.ExecuteSqlRawAsync(
            """UPDATE asset_processing_jobs SET "Result" = {1}::jsonb WHERE "Id" = {0}""",
            [jobId, oversize]);
        (await oversizeAct.Should().ThrowAsync<PostgresException>())
            .Which.SqlState.Should().Be(CHECK_VIOLATION_SQLSTATE);
    }

    private static async Task<(Guid JobId, Guid LeaseToken)> Claim(AssetProcessingJobStore store, Guid expectedJobId)
    {
        IReadOnlyList<ClaimedAssetProcessingJob> batch = await store.ClaimPendingBatch(1, _leaseDuration, "worker-test");
        ClaimedAssetProcessingJob job = batch.Should().ContainSingle(j => j.JobId == expectedJobId).Subject;
        return (job.JobId, job.LeaseToken);
    }

    private static void MakeAvailableNow(ApplicationDbContext db, Guid jobId)
    {
        db.Database.ExecuteSqlRaw(
            """UPDATE asset_processing_jobs SET "AvailableAt" = CURRENT_TIMESTAMP - INTERVAL '1 second' WHERE "Id" = {0}""",
            jobId);
    }

    private static async Task<DateTimeOffset?> GetLeaseExpiry(ApplicationDbContext db, Guid jobId)
    {
        db.ChangeTracker.Clear();
        return (await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == jobId)).LeaseExpiresAt;
    }

    private static void ExpireLease(ApplicationDbContext db, Guid jobId)
    {
        db.Database.ExecuteSqlRaw(
            """UPDATE asset_processing_jobs SET "LeaseExpiresAt" = CURRENT_TIMESTAMP - INTERVAL '1 second' WHERE "Id" = {0}""",
            jobId);
    }

    private static void SetAttemptCount(ApplicationDbContext db, Guid jobId, int maxAttempts, int attemptCount)
    {
        db.Database.ExecuteSqlRaw(
            """UPDATE asset_processing_jobs SET "MaxAttempts" = {1}, "AttemptCount" = {2} WHERE "Id" = {0}""",
            [jobId, maxAttempts, attemptCount]);
    }

    private static void SetMaxAttempts(ApplicationDbContext db, Guid jobId, int maxAttempts)
    {
        db.Database.ExecuteSqlRaw(
            """UPDATE asset_processing_jobs SET "MaxAttempts" = {1} WHERE "Id" = {0} AND "AttemptCount" <= {1}""",
            [jobId, maxAttempts]);
    }
}
