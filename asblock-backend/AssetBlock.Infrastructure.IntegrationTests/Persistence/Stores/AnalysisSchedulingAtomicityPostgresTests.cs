using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Proves a recovered analysis scheduling intent can only ever produce one complete coherent
/// run/job pair: every write happens inside one transaction under the Asset/workspace/version
/// locks, the full current security proof is reread under those locks, a replay resolves the same
/// pair, and a stale, deleted or superseded target schedules nothing.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class AnalysisSchedulingAtomicityPostgresTests(PostgresFixture fixture)
{
    private const string POLICY_KEY = "code-analysis-policy-v1";
    private const string SERVICE_KEY = "code-analysis";

    private static readonly byte[] _content = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
    private static readonly string _contentSha256 =
        Convert.ToHexString(SHA256.HashData(_content)).ToLowerInvariant();

    private static string NewEvidenceDigest() =>
        Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant();

    [Fact]
    public async Task Enqueue_WhenJobInsertFaults_ShouldLeaveNoOrphanRun()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeededProof seed = await SeedReadyWithProof(db);

        // MaxAttempts = 0 violates CK_asset_processing_jobs_max_attempts, so the job insert fails
        // after the run insert has already executed inside the same transaction.
        IAnalysisRunEnqueuer faulty = CreateEnqueuer(
            db, seed.PolicyDigest, new AssetProcessingOptions { MaxAttempts = 0 });

        Func<Task> act = () => faulty.Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        await act.Should().ThrowAsync<Exception>();

        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(0);
    }

    [Fact]
    public async Task Enqueue_WhenAmbientTransactionRollsBack_ShouldPersistNeitherRunNorJob()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeededProof seed = await SeedReadyWithProof(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);

        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction ambient =
            await db.Database.BeginTransactionAsync();

        AnalysisRunEnqueueResult enqueued = await enqueuer.Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.AnalysisRunId.Should().NotBe(Guid.Empty);
        enqueued.AnalysisJobId.Should().NotBe(Guid.Empty);

        // The run insert, job insert and binding all joined the caller's transaction, so rolling
        // it back removes the pair instead of leaving one half behind.
        await ambient.RollbackAsync();

        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(0);
    }

    [Fact]
    public async Task Enqueue_WhenFaultCleared_ShouldRecoverExactlyOneCoherentPair()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeededProof seed = await SeedReadyWithProof(db);

        IAnalysisRunEnqueuer faulty = CreateEnqueuer(
            db, seed.PolicyDigest, new AssetProcessingOptions { MaxAttempts = 0 });
        Func<Task> faulted = () => faulty.Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);
        await faulted.Should().ThrowAsync<Exception>();

        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);
        AnalysisRunEnqueueResult recovered = await enqueuer.Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        recovered.Rejection.Should().Be(AnalysisEnqueueRejection.NONE);
        recovered.IsReplay.Should().BeFalse();

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking()
            .SingleAsync(r => r.Id == recovered.AnalysisRunId);
        AssetProcessingJob job = await db.AssetProcessingJobs.AsNoTracking()
            .SingleAsync(j => j.Id == recovered.AnalysisJobId);

        run.JobId.Should().Be(job.Id);
        job.AnalysisRunId.Should().Be(run.Id);
        run.State.Should().Be(CodeAnalysisRunState.QUEUED);
        job.Status.Should().Be(AssetProcessingJobStatus.QUEUED);

        // A second dispatch over the same intent resolves the very same pair instead of a twin.
        AnalysisRunEnqueueResult replay = await enqueuer.Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        replay.IsReplay.Should().BeTrue();
        replay.AnalysisRunId.Should().Be(recovered.AnalysisRunId);
        replay.AnalysisJobId.Should().Be(recovered.AnalysisJobId);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);
        (await db.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(1);
    }

    [Fact]
    public async Task Handle_WhenProofIsCurrent_ShouldEnqueueExactlyOneBoundRunAndJob()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeededProof seed = await SeedReadyWithProof(db);
        Guid parentRunId = await SeedFailedParentRun(db, seed);
        OutboxMessage intent = await PersistIntent(db, seed, parentRunId);

        await CreateHandler(db, seed.PolicyDigest).Handle(intent, CancellationToken.None);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.ParentRunId == parentRunId);
        AssetProcessingJob job = await db.AssetProcessingJobs.AsNoTracking()
            .SingleAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS);

        run.ArchiveEvidenceId.Should().Be(seed.ArchiveEvidenceId);
        run.MalwareEvidenceId.Should().Be(seed.MalwareEvidenceId);
        run.ParentRunId.Should().Be(parentRunId);
        run.JobId.Should().Be(job.Id);
        job.AnalysisRunId.Should().Be(run.Id);
    }

    [Fact]
    public async Task Handle_WhenProofSupersededBySecurityRecheck_ShouldScheduleNothing()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeededProof seed = await SeedReadyWithProof(db);
        OutboxMessage intent = await PersistIntent(db, seed);

        // A started recheck moves the selection immediately, so the intent's generation-1 proof is
        // no longer the current proof even though every evidence row still exists unchanged.
        db.SecurityRechecks.Add(new SecurityRecheck
        {
            Id = Guid.NewGuid(),
            AssetId = seed.AssetId,
            AssetVersionId = seed.VersionId,
            ContentSha256 = _contentSha256,
            SecurityGeneration = 2,
            SecurityPolicyVersion = "policy-v1",
            DefinitionVersion = 1,
            OperationDigest = new string('a', 64),
            OperationReceiptId = Guid.NewGuid(),
            State = SecurityRecheckState.SUCCEEDED,
            SelectedGeneration = 2,
            CompletedGeneration = 2,
            RequestedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        await CreateHandler(db, seed.PolicyDigest).Handle(intent, CancellationToken.None);

        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenScannerSignatureExpired_ShouldThrowWithoutScheduling()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeededProof seed = await SeedReadyWithProof(
            db, signatureObservedAt: DateTimeOffset.UtcNow.AddDays(-5),
            signatureValidUntil: DateTimeOffset.UtcNow.AddDays(-1));
        OutboxMessage intent = await PersistIntent(db, seed);

        Func<Task> act = () => CreateHandler(db, seed.PolicyDigest).Handle(intent, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*PROOF_INVALID*");

        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenAssetDeleted_ShouldScheduleNothing()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeededProof seed = await SeedReadyWithProof(db);
        OutboxMessage intent = await PersistIntent(db, seed);

        await db.Assets.Where(a => a.Id == seed.AssetId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, DateTimeOffset.UtcNow));

        await CreateHandler(db, seed.PolicyDigest).Handle(intent, CancellationToken.None);

        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Handle_WhenReplayedConcurrently_ShouldCreateExactlyOneCoherentPair()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeededProof seed = await SeedReadyWithProof(seedDb);
        OutboxMessage intent = await PersistIntent(seedDb, seed);

        await using ApplicationDbContext firstDb = fixture.CreateDbContext();
        await using ApplicationDbContext secondDb = fixture.CreateDbContext();

        await Task.WhenAll(
            CreateHandler(firstDb, seed.PolicyDigest).Handle(intent, CancellationToken.None),
            CreateHandler(secondDb, seed.PolicyDigest).Handle(intent, CancellationToken.None));

        (await seedDb.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);
        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync();
        AssetProcessingJob job = await seedDb.AssetProcessingJobs.AsNoTracking()
            .SingleAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS);
        run.JobId.Should().Be(job.Id);
        job.AnalysisRunId.Should().Be(run.Id);
    }

    [Fact]
    public async Task Enqueue_WhenNewerGenerationArrives_ShouldSupersedeQueuedRun()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeededProof seed = await SeedReadyWithProof(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);

        AnalysisRunEnqueueResult first = await enqueuer.Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);
        first.Rejection.Should().Be(AnalysisEnqueueRejection.NONE);

        (Guid recheckId, Guid archiveId, Guid malwareId) = await SeedSelectedRecheck(db, seed);

        AnalysisRunEnqueueResult newer = await enqueuer.Enqueue(
            seed.AssetId, seed.VersionId, recheckId, archiveId, malwareId,
            2, _contentSha256, DateTimeOffset.UtcNow);

        newer.Rejection.Should().Be(AnalysisEnqueueRejection.NONE);
        newer.AnalysisRunId.Should().NotBe(first.AnalysisRunId);

        CodeAnalysisRun superseded = await db.CodeAnalysisRuns.AsNoTracking()
            .SingleAsync(r => r.Id == first.AnalysisRunId);
        superseded.State.Should().Be(CodeAnalysisRunState.CANCELLED);
        superseded.Disposition.Should().Be(CodeAnalysisReportDisposition.HISTORICAL_SUPERSEDED);

        (await db.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.Id == first.AnalysisJobId && j.Status == AssetProcessingJobStatus.CANCELLED))
            .Should().Be(1);
        (await db.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.AnalysisRunId == newer.AnalysisRunId && j.Status == AssetProcessingJobStatus.QUEUED))
            .Should().Be(1);
    }

    [Fact]
    public async Task Enqueue_WhenOlderRunIsExecuting_ShouldDeferWithoutScheduling()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeededProof seed = await SeedReadyWithProof(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);

        AnalysisRunEnqueueResult first = await enqueuer.Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        await db.CodeAnalysisRuns.Where(r => r.Id == first.AnalysisRunId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, CodeAnalysisRunState.RUNNING)
                .SetProperty(r => r.UpdatedAt, DateTimeOffset.UtcNow));

        (Guid recheckId, Guid archiveId, Guid malwareId) = await SeedSelectedRecheck(db, seed);

        AnalysisRunEnqueueResult newer = await enqueuer.Enqueue(
            seed.AssetId, seed.VersionId, recheckId, archiveId, malwareId,
            2, _contentSha256, DateTimeOffset.UtcNow);

        // An actively leased run is never killed under its worker; recovery retries instead.
        newer.Rejection.Should().Be(AnalysisEnqueueRejection.PENDING_LIVE_RUN);
        newer.AnalysisRunId.Should().Be(Guid.Empty);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);
    }

#pragma warning disable IDE0007
    [Fact]
    public async Task Retry_AfterRecoveryCrossesMaxAttempts_ShouldExposeRetryAndAcceptLinkedChild()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (SeededProof seed, Guid ownerId, Guid failedRunId, var workspaceRevision) = await SeedFailedRetryContext(db);
        const string serviceKey = SERVICE_KEY;
        var transportToken = NewEvidenceDigest();
        var leaseToken = Guid.NewGuid();
        DateTimeOffset expiredAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        await CreateCapacity(db).EnsureSlots(serviceKey, 1);
        CodeAnalysisRun originalRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        AssetProcessingJob originalJob = await db.AssetProcessingJobs.AsNoTracking()
            .SingleAsync(j => j.AnalysisRunId == failedRunId);
        await db.CodeAnalysisRuns.Where(r => r.Id == failedRunId).ExecuteUpdateAsync(s => s
            .SetProperty(r => r.State, CodeAnalysisRunState.RUNNING)
            .SetProperty(r => r.OwnerRetryable, false)
            .SetProperty(r => r.CompletedAt, (DateTimeOffset?)null)
            .SetProperty(r => r.TransportLeaseToken, transportToken)
            .SetProperty(r => r.ReservationGeneration, 1)
            .SetProperty(r => r.HardDeadlineUtc, expiredAt));
        await db.AssetProcessingJobs.Where(j => j.Id == originalJob.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, AssetProcessingJobStatus.RUNNING)
            .SetProperty(j => j.Stage, "ANALYZING")
            .SetProperty(j => j.AttemptCount, 2)
            .SetProperty(j => j.LeaseOwner, "recovery-test")
            .SetProperty(j => j.LeaseToken, leaseToken)
            .SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(1))
            .SetProperty(j => j.TransportLeaseToken, transportToken)
            .SetProperty(j => j.CompletedAt, (DateTimeOffset?)null));
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE analysis_capacity_slots
            SET "State" = 'RESERVED',
                "ReservedRunId" = {failedRunId},
                "ReservedJobId" = {originalJob.Id},
                "LeaseToken" = {transportToken},
                "ReservationGeneration" = 1,
                "HardDeadlineUtc" = {expiredAt},
                "CleanupGraceUntil" = {expiredAt},
                "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "ServiceKey" = {serviceKey} AND "State" = 'FREE'
            """);

        (await CreateCapacity(db).RecoverExpiredReservations()).Should().Be(1);

        CodeAnalysisRun recoveredRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        AssetProcessingJob recoveredJob = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == originalJob.Id);
        recoveredRun.State.Should().Be(CodeAnalysisRunState.FAILED);
        recoveredRun.OwnerRetryable.Should().BeTrue();
        recoveredJob.Status.Should().Be(AssetProcessingJobStatus.FAILED);
        recoveredJob.AttemptCount.Should().Be(3);

        CodeAnalysisSellerStore store = CreateSellerStore(db, seed);
        SellerAnalysisSummaryDto summary = (await store.GetOwnerSummary(seed.VersionId, ownerId))!;
        summary.Lifecycle.Should().Be(SellerAnalysisLifecycle.FAILED);
        summary.CanRetry.Should().BeTrue();
        summary.RetryExpectation.Should().NotBeNull();
        summary.RetryExpectation!.RunId.Should().Be(failedRunId);
        summary.RetryExpectation.WorkspaceRevision.Should().Be(workspaceRevision);

        SellerAnalysisRetryRequest request = RetryRequest(
            failedRunId, summary.RetryExpectation.WorkspaceRevision, recoveredRun.PolicyDigest, recoveredRun.InputDigest);
        SellerAnalysisRetryStoreResult accepted = await store.Retry(seed.AssetId, seed.VersionId, ownerId, request);
        accepted.Status.Should().Be(SellerAnalysisRetryStatus.ACCEPTED);
        accepted.Accepted.Should().NotBeNull();

        CodeAnalysisRun child = await db.CodeAnalysisRuns.AsNoTracking()
            .SingleAsync(r => r.Id == accepted.Accepted!.RunId);
        child.ParentRunId.Should().Be(failedRunId);
        child.State.Should().Be(CodeAnalysisRunState.QUEUED);
        (await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.AnalysisRunId == child.Id)).Status
            .Should().Be(AssetProcessingJobStatus.QUEUED);
        originalRun.State.Should().Be(CodeAnalysisRunState.FAILED);
    }

    [Fact]
    public async Task Retry_WhenSameOperationRunsConcurrently_ShouldReturnOneCommittedChildAndReceipt()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (SeededProof seed, Guid ownerId, Guid failedRunId, var workspaceRevision) = await SeedFailedRetryContext(seedDb);
        CodeAnalysisRun failedRun = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        SellerAnalysisRetryRequest request = RetryRequest(failedRunId, workspaceRevision, failedRun.PolicyDigest, failedRun.InputDigest);
        await using ApplicationDbContext firstDb = fixture.CreateDbContext();
        await using ApplicationDbContext secondDb = fixture.CreateDbContext();

        SellerAnalysisRetryStoreResult[] results = await Task.WhenAll(
            CreateSellerStore(firstDb, seed).Retry(seed.AssetId, seed.VersionId, ownerId, request),
            CreateSellerStore(secondDb, seed).Retry(seed.AssetId, seed.VersionId, ownerId, request));

        results.Should().OnlyContain(x => x.Status == SellerAnalysisRetryStatus.ACCEPTED && x.Accepted != null);
        results.Select(x => x.Accepted!.RunId).Distinct().Should().ContainSingle();
        (await seedDb.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(2);
        (await seedDb.AssetProcessingJobs.AsNoTracking().CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(2);
        (await seedDb.JsonMutationIdempotencyRecords.AsNoTracking().CountAsync(x => x.OperationId == request.OperationId)).Should().Be(1);
        (await seedDb.AuditLogs.AsNoTracking().CountAsync(x => x.Action == "SELLER_ANALYSIS_RETRY")).Should().Be(1);
    }

    [Fact]
    public async Task Retry_WhenReceiptExistsWithDifferentBody_ShouldConflictWithoutExtraEffects()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (SeededProof seed, Guid ownerId, Guid failedRunId, var workspaceRevision) = await SeedFailedRetryContext(db);
        CodeAnalysisRun failedRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        SellerAnalysisRetryRequest request = RetryRequest(failedRunId, workspaceRevision, failedRun.PolicyDigest, failedRun.InputDigest);
        AssetProcessingJob failedJob = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.AnalysisRunId == failedRunId);
        failedJob.Status.Should().Be(AssetProcessingJobStatus.FAILED);
        failedRun.JobId.Should().Be(failedJob.Id);
        CodeAnalysisSellerStore store = CreateSellerStore(db, seed);

        SellerAnalysisRetryStoreResult accepted = await store.Retry(seed.AssetId, seed.VersionId, ownerId, request);
        SellerAnalysisRetryStoreResult conflict = await store.Retry(seed.AssetId, seed.VersionId, ownerId,
            request with { ExpectedWorkspaceRevision = workspaceRevision + 1 });

        accepted.Status.Should().Be(SellerAnalysisRetryStatus.ACCEPTED);
        conflict.Status.Should().Be(SellerAnalysisRetryStatus.CONFLICT);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(2);
        (await db.JsonMutationIdempotencyRecords.AsNoTracking().CountAsync(x => x.OperationId == request.OperationId)).Should().Be(1);
        (await db.AuditLogs.AsNoTracking().CountAsync(x => x.Action == "SELLER_ANALYSIS_RETRY")).Should().Be(1);
    }

    [Fact]
    public async Task Retry_WhenCurrentProofExpired_ShouldQueueBoundRecheckAndReplayAtomicReceipt()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (SeededProof seed, Guid ownerId, Guid failedRunId, var workspaceRevision) =
            await SeedFailedRetryContext(db, DateTimeOffset.UtcNow.AddMinutes(-1));
        CodeAnalysisRun failedRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        SellerAnalysisRetryRequest request = RetryRequest(failedRunId, workspaceRevision, failedRun.PolicyDigest, failedRun.InputDigest);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);
        ISecurityProvenanceStore provenance = new SecurityProvenanceStore(
            db, MsOptions.Create(new AssetProcessingOptions()), enqueuer, NullLogger<SecurityProvenanceStore>.Instance);
        CodeAnalysisSellerStore store = CreateSellerStore(db, seed, provenance);

        SellerAnalysisRetryStoreResult accepted = await store.Retry(seed.AssetId, seed.VersionId, ownerId, request);
        SellerAnalysisRetryStoreResult replay = await store.Retry(seed.AssetId, seed.VersionId, ownerId, request);

        accepted.Status.Should().Be(SellerAnalysisRetryStatus.RECHECK_QUEUED);
        accepted.Accepted.Should().NotBeNull();
        accepted.Accepted!.SecurityRecheckId.Should().NotBeNull();
        replay.Status.Should().Be(SellerAnalysisRetryStatus.RECHECK_QUEUED);
        replay.Accepted.Should().BeEquivalentTo(accepted.Accepted! with { Replayed = true });
        SecurityRecheck recheck = await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.OperationReceiptId == request.OperationId);
        recheck.AssetId.Should().Be(seed.AssetId);
        recheck.AssetVersionId.Should().Be(seed.VersionId);
        recheck.ParentAnalysisRunId.Should().Be(failedRunId);
        recheck.OperationDigest.Should().NotBeNullOrWhiteSpace();
        (await db.AssetProcessingJobs.AsNoTracking().CountAsync(j => j.SecurityRecheckId == recheck.Id
            && j.Type == AssetProcessingJobType.ARCHIVE_INSPECTION && j.Status == AssetProcessingJobStatus.QUEUED)).Should().Be(1);
        (await db.JsonMutationIdempotencyRecords.AsNoTracking().CountAsync(r => r.OperationId == request.OperationId)).Should().Be(1);
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "SELLER_ANALYSIS_RETRY")).Should().Be(1);
    }

    [Fact]
    public async Task Retry_WhenSelectedRecheckEndedOperationally_ShouldQueueLinkedProofRepair()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (SeededProof seed, Guid ownerId, Guid failedRunId, var workspaceRevision) = await SeedFailedRetryContext(db);
        CodeAnalysisRun failedRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        var oldRecheckId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.SecurityRechecks.Add(new SecurityRecheck
        {
            Id = oldRecheckId,
            AssetId = seed.AssetId,
            AssetVersionId = seed.VersionId,
            ContentSha256 = failedRun.ContentSha256,
            SecurityGeneration = 2,
            SecurityPolicyVersion = AssetProcessingDefaults.MALWARE_POLICY_VERSION,
            DefinitionVersion = AssetProcessingDefaults.DEFINITION_VERSION,
            OperationDigest = NewEvidenceDigest(),
            OperationReceiptId = Guid.NewGuid(),
            State = SecurityRecheckState.ERROR,
            SelectedGeneration = 2,
            ParentAnalysisRunId = failedRunId,
            ErrorCode = "SCANNER_UNAVAILABLE",
            ErrorSummary = "scanner unavailable",
            RequestedAt = now,
            CompletedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        SellerAnalysisRetryRequest request = RetryRequest(failedRunId, workspaceRevision, failedRun.PolicyDigest, failedRun.InputDigest);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);
        ISecurityProvenanceStore provenance = new SecurityProvenanceStore(
            db, MsOptions.Create(new AssetProcessingOptions()), enqueuer, NullLogger<SecurityProvenanceStore>.Instance);
        CodeAnalysisSellerStore store = CreateSellerStore(db, seed, provenance);

        SellerAnalysisRetryStoreResult accepted = await store.Retry(seed.AssetId, seed.VersionId, ownerId, request);
        SellerAnalysisRetryStoreResult replay = await store.Retry(seed.AssetId, seed.VersionId, ownerId, request);
        SellerAnalysisRetryStoreResult changed = await store.Retry(seed.AssetId, seed.VersionId, ownerId,
            request with { ExpectedInputDigest = NewEvidenceDigest() });

        accepted.Status.Should().Be(SellerAnalysisRetryStatus.RECHECK_QUEUED);
        replay.Accepted.Should().BeEquivalentTo(accepted.Accepted! with { Replayed = true });
        changed.Status.Should().Be(SellerAnalysisRetryStatus.CONFLICT);
        SecurityRecheck[] rechecks = await db.SecurityRechecks.AsNoTracking().OrderBy(r => r.SecurityGeneration).ToArrayAsync();
        rechecks.Should().HaveCount(2);
        rechecks[0].State.Should().Be(SecurityRecheckState.ERROR);
        rechecks[0].SelectedGeneration.Should().BeNull();
        rechecks[1].ParentRecheckId.Should().Be(oldRecheckId);
        rechecks[1].ParentAnalysisRunId.Should().Be(failedRunId);
        rechecks[1].State.Should().Be(SecurityRecheckState.QUEUED);
        (await db.AssetVersions.AsNoTracking().Where(v => v.Id == seed.VersionId)
            .Select(v => v.SafetyRestriction).SingleAsync()).Should().Be(AssetVersionSafetyRestriction.NONE);
        (await db.JsonMutationIdempotencyRecords.AsNoTracking().CountAsync(r => r.OperationId == request.OperationId)).Should().Be(1);
    }

    [Fact]
    public async Task Retry_WhenSelectedSuccessfulProofExpired_ShouldQueueFreshProofGeneration()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (SeededProof seed, Guid ownerId, Guid failedRunId, var workspaceRevision) = await SeedFailedRetryContext(db);
        CodeAnalysisRun failedRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        AssetVersion version = await db.AssetVersions.SingleAsync(v => v.Id == seed.VersionId);
        var selectedId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var selected = new SecurityRecheck
        {
            Id = selectedId,
            AssetId = seed.AssetId,
            AssetVersionId = seed.VersionId,
            ContentSha256 = failedRun.ContentSha256,
            SecurityGeneration = 2,
            SecurityPolicyVersion = AssetProcessingDefaults.MALWARE_POLICY_VERSION,
            DefinitionVersion = AssetProcessingDefaults.DEFINITION_VERSION,
            OperationDigest = NewEvidenceDigest(),
            OperationReceiptId = Guid.NewGuid(),
            State = SecurityRecheckState.SUCCEEDED,
            SelectedGeneration = 2,
            CompletedGeneration = 2,
            RequestedAt = now,
            CompletedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SecurityRechecks.Add(selected);
        await db.SaveChangesAsync();

        AdmittedReportTestEvidence.Chain selectedProofChain = AdmittedReportTestEvidence.BuildChain();
        (SecurityEvidenceRecord _, SecurityEvidenceRecord selectedMalware) = await AdmittedReportTestEvidence.SeedObservedProofAsync(
            db, version, selectedProofChain, selectedId, generation: 2);
        selected.ArchiveEvidenceId = selectedProofChain.ArchiveEvidenceId;
        selected.MalwareEvidenceId = selectedProofChain.MalwareEvidenceId;
        await db.SaveChangesAsync();
        await db.SecurityEvidenceRecords.Where(e => e.Id == selectedMalware.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.SignatureValidUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));

        SellerAnalysisRetryRequest request = RetryRequest(failedRunId, workspaceRevision, failedRun.PolicyDigest, failedRun.InputDigest);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);
        ISecurityProvenanceStore provenance = new SecurityProvenanceStore(
            db, MsOptions.Create(new AssetProcessingOptions()), enqueuer, NullLogger<SecurityProvenanceStore>.Instance);
        CodeAnalysisSellerStore store = CreateSellerStore(db, seed, provenance);

        SellerAnalysisRetryStoreResult accepted = await store.Retry(seed.AssetId, seed.VersionId, ownerId, request);

        accepted.Status.Should().Be(SellerAnalysisRetryStatus.RECHECK_QUEUED);
        SecurityRecheck[] rechecks = await db.SecurityRechecks.AsNoTracking().OrderBy(r => r.SecurityGeneration).ToArrayAsync();
        rechecks.Should().HaveCount(2);
        rechecks[0].State.Should().Be(SecurityRecheckState.SUCCEEDED);
        rechecks[0].SelectedGeneration.Should().BeNull();
        rechecks[1].SecurityGeneration.Should().Be(3);
        rechecks[1].ParentRecheckId.Should().BeNull();
        rechecks[1].ParentAnalysisRunId.Should().Be(failedRunId);
        rechecks[1].SelectedGeneration.Should().Be(3);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Retry_WhenAnotherSelectedRecheckIsActive_ShouldNotCreateDuplicateRepair()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (SeededProof seed, Guid ownerId, Guid failedRunId, var workspaceRevision) = await SeedFailedRetryContext(db);
        CodeAnalysisRun failedRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.SecurityRechecks.Add(new SecurityRecheck
        {
            Id = Guid.NewGuid(),
            AssetId = seed.AssetId,
            AssetVersionId = seed.VersionId,
            ContentSha256 = failedRun.ContentSha256,
            SecurityGeneration = 2,
            SecurityPolicyVersion = AssetProcessingDefaults.MALWARE_POLICY_VERSION,
            DefinitionVersion = AssetProcessingDefaults.DEFINITION_VERSION,
            OperationDigest = NewEvidenceDigest(),
            OperationReceiptId = Guid.NewGuid(),
            State = SecurityRecheckState.SCANNING,
            SelectedGeneration = 2,
            ParentAnalysisRunId = failedRunId,
            RequestedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        SellerAnalysisRetryRequest request = RetryRequest(failedRunId, workspaceRevision, failedRun.PolicyDigest, failedRun.InputDigest);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);
        ISecurityProvenanceStore provenance = new SecurityProvenanceStore(
            db, MsOptions.Create(new AssetProcessingOptions()), enqueuer, NullLogger<SecurityProvenanceStore>.Instance);
        CodeAnalysisSellerStore store = CreateSellerStore(db, seed, provenance);

        SellerAnalysisRetryStoreResult result = await store.Retry(seed.AssetId, seed.VersionId, ownerId, request);

        result.Status.Should().Be(SellerAnalysisRetryStatus.CONFLICT);
        (await db.SecurityRechecks.AsNoTracking().CountAsync()).Should().Be(1);
        (await db.JsonMutationIdempotencyRecords.AsNoTracking().CountAsync(r => r.OperationId == request.OperationId)).Should().Be(0);
        (await db.AssetVersions.AsNoTracking().Where(v => v.Id == seed.VersionId)
            .Select(v => v.SafetyRestriction).SingleAsync()).Should().Be(AssetVersionSafetyRestriction.NONE);
    }

    [Theory]
    [InlineData("edit")]
    [InlineData("delete")]
    [InlineData("quarantine")]
    public async Task Retry_WhenCurrentSellerOrSafetyStateChanged_ShouldConflictWithoutEffects(string change)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (SeededProof seed, Guid ownerId, Guid failedRunId, var workspaceRevision) = await SeedFailedRetryContext(db);
        CodeAnalysisRun failedRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        SellerAnalysisRetryRequest request = RetryRequest(failedRunId, workspaceRevision, failedRun.PolicyDigest, failedRun.InputDigest);
        switch (change)
        {
            case "edit":
                await db.AssetDraftWorkspaces.Where(w => w.AssetId == seed.AssetId && w.AssetVersionId == seed.VersionId)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.WorkspaceRevision, w => w.WorkspaceRevision + 1));
                break;
            case "delete":
                await db.Assets.Where(a => a.Id == seed.AssetId)
                    .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, DateTimeOffset.UtcNow));
                break;
            case "quarantine":
                await db.AssetVersions.Where(v => v.Id == seed.VersionId).ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.SafetyRestriction, AssetVersionSafetyRestriction.INTEGRITY_MISMATCH)
                    .SetProperty(v => v.SafetyRestrictionReason, "ARCHIVE_CORRUPT")
                    .SetProperty(v => v.SafetyRestrictedAt, DateTimeOffset.UtcNow));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(change));
        }
        db.ChangeTracker.Clear();
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);
        ISecurityProvenanceStore provenance = new SecurityProvenanceStore(
            db, MsOptions.Create(new AssetProcessingOptions()), enqueuer, NullLogger<SecurityProvenanceStore>.Instance);
        CodeAnalysisSellerStore store = CreateSellerStore(db, seed, provenance);

        SellerAnalysisRetryStoreResult result = await store.Retry(seed.AssetId, seed.VersionId, ownerId, request);

        result.Status.Should().Be(change == "delete"
            ? SellerAnalysisRetryStatus.NOT_FOUND
            : SellerAnalysisRetryStatus.CONFLICT);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);
        (await db.SecurityRechecks.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.AssetProcessingJobs.AsNoTracking().CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(1);
        (await db.JsonMutationIdempotencyRecords.AsNoTracking().CountAsync(r => r.OperationId == request.OperationId)).Should().Be(0);
    }

    [Fact]
    public async Task Retry_WhenAuditWriteFailsDuringProofRepair_ShouldRollbackRecheckJobAndReceipt()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (SeededProof seed, Guid ownerId, Guid failedRunId, var workspaceRevision) =
            await SeedFailedRetryContext(db, DateTimeOffset.UtcNow.AddMinutes(-1));
        CodeAnalysisRun failedRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        SellerAnalysisRetryRequest request = RetryRequest(failedRunId, workspaceRevision, failedRun.PolicyDigest, failedRun.InputDigest);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);
        ISecurityProvenanceStore provenance = new SecurityProvenanceStore(
            db, MsOptions.Create(new AssetProcessingOptions()), enqueuer, NullLogger<SecurityProvenanceStore>.Instance);
        CodeAnalysisSellerStore store = CreateSellerStore(db, seed, provenance, new ThrowingAuditWriter());

        Func<Task> act = () => store.Retry(seed.AssetId, seed.VersionId, ownerId, request);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Synthetic audit failure.");

        (await db.SecurityRechecks.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.AssetProcessingJobs.AsNoTracking().CountAsync(j => j.SecurityRecheckId != null)).Should().Be(0);
        (await db.JsonMutationIdempotencyRecords.AsNoTracking().CountAsync(r => r.OperationId == request.OperationId)).Should().Be(0);
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "SELLER_ANALYSIS_RETRY")).Should().Be(0);
        (await db.AssetVersions.AsNoTracking().Where(v => v.Id == seed.VersionId)
            .Select(v => v.AnalysisSelectionGeneration).SingleAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Retry_WhenDifferentOperationTargetsReplacedParent_ShouldConflictWithoutExtraEffects()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (SeededProof seed, Guid ownerId, Guid failedRunId, var workspaceRevision) = await SeedFailedRetryContext(db);
        CodeAnalysisRun failedRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == failedRunId);
        SellerAnalysisRetryRequest firstRequest = RetryRequest(failedRunId, workspaceRevision, failedRun.PolicyDigest, failedRun.InputDigest);
        CodeAnalysisSellerStore store = CreateSellerStore(db, seed);

        SellerAnalysisRetryStoreResult first = await store.Retry(seed.AssetId, seed.VersionId, ownerId, firstRequest);
        SellerAnalysisRetryStoreResult obsolete = await store.Retry(seed.AssetId, seed.VersionId, ownerId,
            firstRequest with { OperationId = Guid.NewGuid() });

        first.Status.Should().Be(SellerAnalysisRetryStatus.ACCEPTED);
        obsolete.Status.Should().Be(SellerAnalysisRetryStatus.CONFLICT);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(2);
        (await db.AssetProcessingJobs.AsNoTracking().CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(2);
        (await db.JsonMutationIdempotencyRecords.AsNoTracking().CountAsync()).Should().Be(1);
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "SELLER_ANALYSIS_RETRY")).Should().Be(1);
    }
#pragma warning restore IDE0007

    private sealed class ThrowingAuditWriter : IAuditWriter
    {
        public Task Write(AuditEvent auditEvent, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Synthetic audit failure."));

        public Task WriteBestEffort(AuditEvent auditEvent, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private static AnalysisRunEnqueuer CreateEnqueuer(
        ApplicationDbContext db,
        string policyDigest,
        AssetProcessingOptions? processing = null) =>
        new(db,
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = SERVICE_KEY,
                PolicyKey = POLICY_KEY,
                PolicyDigest = policyDigest
            }),
            MsOptions.Create(processing ?? new AssetProcessingOptions()),
            NullLogger<AnalysisRunEnqueuer>.Instance);

    private static AnalysisCapacityStore CreateCapacity(ApplicationDbContext db) => new(
        db,
        MsOptions.Create(new CodeAnalysisOptions { Enabled = true, ServiceKey = SERVICE_KEY, Capacity = 1 }),
        NullLogger<AnalysisCapacityStore>.Instance);

    private static AnalysisSchedulingOutboxHandler CreateHandler(ApplicationDbContext db, string policyDigest) =>
        new(db,
            CreateEnqueuer(db, policyDigest),
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = SERVICE_KEY,
                PolicyKey = POLICY_KEY,
                PolicyDigest = policyDigest
            }),
            NullLogger<AnalysisSchedulingOutboxHandler>.Instance);

    private static async Task<OutboxMessage> PersistIntent(ApplicationDbContext db, SeededProof seed, Guid? parentRunId = null)
    {
        await CreateEnqueuer(db, seed.PolicyDigest).PersistSchedulingIntent(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow, parentRunId);

        return await db.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Type == OutboxMessageTypes.ANALYSIS_SCHEDULING);
    }

    private static async Task<Guid> SeedFailedParentRun(ApplicationDbContext db, SeededProof seed)
    {
        CodeAnalysisPolicyVersion policy = await db.CodeAnalysisPolicyVersions.AsNoTracking()
            .SingleAsync(p => p.PolicyDigest == seed.PolicyDigest);
        var parentRunId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.CodeAnalysisRuns.Add(new CodeAnalysisRun
        {
            Id = parentRunId,
            AssetId = seed.AssetId,
            AssetVersionId = seed.VersionId,
            ContentSha256 = _contentSha256,
            PolicyVersionId = policy.Id,
            PolicyDigest = seed.PolicyDigest,
            DeclarationDigest = NewEvidenceDigest(),
            MaterialDigest = NewEvidenceDigest(),
            InputDigest = NewEvidenceDigest(),
            SelectionGeneration = 1,
            ArchiveEvidenceId = seed.ArchiveEvidenceId,
            MalwareEvidenceId = seed.MalwareEvidenceId,
            SecurityGeneration = 1,
            ServiceKey = SERVICE_KEY,
            State = CodeAnalysisRunState.FAILED,
            ErrorCode = "ANALYSIS_CLIENT_ERROR",
            ErrorSummary = "Synthetic retry parent for scheduling recovery test.",
            OwnerRetryable = true,
            CreatedAt = now,
            CompletedAt = now
        });
        await db.SaveChangesAsync();
        return parentRunId;
    }

    private static SellerAnalysisRetryRequest RetryRequest(Guid failedRunId, long workspaceRevision, string policyDigest, string inputDigest) => new(
        Guid.NewGuid(), failedRunId, workspaceRevision, policyDigest, inputDigest);

    private static CodeAnalysisSellerStore CreateSellerStore(
        ApplicationDbContext db,
        SeededProof seed,
        ISecurityProvenanceStore? securityProvenance = null,
        IAuditWriter? auditWriter = null) => new(
        db,
        CreateEnqueuer(db, seed.PolicyDigest),
        securityProvenance ?? Substitute.For<ISecurityProvenanceStore>(),
        auditWriter ?? new AuditWriter(new AuditStore(db), new NullAuditContextAccessor(), NullLogger<AuditWriter>.Instance),
        TimeProvider.System,
        MsOptions.Create(new CodeAnalysisOptions
        {
            Enabled = true,
            ServiceKey = SERVICE_KEY,
            PolicyKey = POLICY_KEY,
            PolicyDigest = seed.PolicyDigest
        }));

    private static async Task<(SeededProof Seed, Guid OwnerId, Guid FailedRunId, long WorkspaceRevision)> SeedFailedRetryContext(
        ApplicationDbContext db,
        DateTimeOffset? signatureValidUntil = null)
    {
        SeededProof seed = await SeedReadyWithProof(db, signatureValidUntil: signatureValidUntil);
        AssetVersion version = await db.AssetVersions.SingleAsync(v => v.Id == seed.VersionId);
        Asset asset = await db.Assets.SingleAsync(a => a.Id == seed.AssetId);
        var workspaceId = Guid.NewGuid();
        db.AssetDraftWorkspaces.Add(new AssetDraftWorkspace
        {
            Id = workspaceId,
            AssetId = seed.AssetId,
            AssetVersionId = seed.VersionId,
            WorkspaceVersionScopeKey = seed.VersionId,
            ScopeId = Guid.NewGuid(),
            WorkspaceRevision = 1
        });

        CodeAnalysisPolicyVersion policy = await db.CodeAnalysisPolicyVersions.SingleAsync(p => p.PolicyDigest == seed.PolicyDigest);
        var failedRunId = Guid.NewGuid();
        var failedJobId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        db.CodeAnalysisRuns.Add(new CodeAnalysisRun
        {
            Id = failedRunId,
            AssetId = seed.AssetId,
            AssetVersionId = seed.VersionId,
            AssetDraftWorkspaceId = workspaceId,
            ContentSha256 = version.ContentSha256,
            PolicyVersionId = policy.Id,
            PolicyDigest = seed.PolicyDigest,
            DeclarationDigest = NewEvidenceDigest(),
            MaterialDigest = NewEvidenceDigest(),
            InputDigest = NewEvidenceDigest(),
            SelectionGeneration = version.AnalysisSelectionGeneration,
            ArchiveEvidenceId = seed.ArchiveEvidenceId,
            MalwareEvidenceId = seed.MalwareEvidenceId,
            SecurityGeneration = 1,
            ServiceKey = SERVICE_KEY,
            State = CodeAnalysisRunState.FAILED,
            ErrorCode = "ANALYSIS_CLIENT_ERROR",
            ErrorSummary = "Retryable failure.",
            OwnerRetryable = true,
            CompletedAt = now,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        db.AssetProcessingJobs.Add(new AssetProcessingJob
        {
            Id = failedJobId,
            AssetId = seed.AssetId,
            AssetVersionId = seed.VersionId,
            Type = AssetProcessingJobType.CODE_ANALYSIS,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.FAILED,
            Stage = "FAILED",
            AttemptCount = 3,
            MaxAttempts = 3,
            ErrorCode = "ANALYSIS_CLIENT_ERROR",
            ErrorSummary = "Retryable failure.",
            AvailableAt = now,
            CompletedAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            AnalysisRunId = failedRunId,
            Payload = AssetProcessingSerializer.SerializePayload(AssetProcessingJobType.CODE_ANALYSIS,
                new CodeAnalysisPayload(failedRunId, NewEvidenceDigest(), seed.PolicyDigest, version.ContentSha256,
                    seed.ArchiveEvidenceId, seed.MalwareEvidenceId, 1))
        });
        await db.SaveChangesAsync();
        await db.CodeAnalysisRuns.Where(r => r.Id == failedRunId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, failedJobId));
        return (seed, asset.AuthorId, failedRunId, 1);
    }

    private sealed record SeededProof(
        Guid AssetId,
        Guid VersionId,
        Guid ArchiveEvidenceId,
        Guid MalwareEvidenceId,
        string PolicyDigest);

    private static async Task<SeededProof> SeedReadyWithProof(
        ApplicationDbContext db,
        DateTimeOffset? signatureObservedAt = null,
        DateTimeOffset? signatureValidUntil = null)
    {
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));

        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Recovered scheduling subject");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        version.ContentLength = _content.Length;
        version.ContentSha256 = _contentSha256;
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var archiveId = Guid.NewGuid();
        var malwareId = Guid.NewGuid();

        // The proof is admitted only with the producing jobs that observed it: both jobs are
        // already succeeded, and the scanner job carries the approved policy in its payload.
        AssetProcessingJob archiveJob = ProducerJob(
            asset.Id, version.Id, AssetProcessingJobType.ARCHIVE_INSPECTION,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.ARCHIVE_INSPECTION, new ArchiveInspectionPayload()),
            securityRecheckId: null, now);
        AssetProcessingJob malwareJob = ProducerJob(
            asset.Id, version.Id, AssetProcessingJobType.MALWARE_SCAN,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.MALWARE_SCAN,
                new MalwareScanPayload(AssetProcessingDefaults.MALWARE_POLICY_VERSION)),
            securityRecheckId: null, now);
        db.AssetProcessingJobs.AddRange(archiveJob, malwareJob);

        db.SecurityEvidenceRecords.Add(new SecurityEvidenceRecord
        {
            Id = archiveId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = _contentSha256,
            JobId = archiveJob.Id,
            EvidenceKind = SecurityEvidenceKind.ARCHIVE_VALIDATION,
            Generation = 1,
            DefinitionVersion = 1,
            Outcome = SecurityEvidenceOutcome.VALID,
            SecurityPolicyVersion = AssetProcessingDefaults.MALWARE_POLICY_VERSION,
            ImplementationIdentity = "archive-inspector",
            IdentityAvailable = false,
            ScannedByteCount = _content.Length,
            ScannedEntryCount = 3,
            ScopeComplete = true,
            CompletedAt = now,
            EvidenceDigest = NewEvidenceDigest(),
            CreatedAt = now
        });
        db.SecurityEvidenceRecords.Add(new SecurityEvidenceRecord
        {
            Id = malwareId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = _contentSha256,
            JobId = malwareJob.Id,
            EvidenceKind = SecurityEvidenceKind.MALWARE_SCAN,
            Generation = 1,
            DefinitionVersion = 1,
            Outcome = SecurityEvidenceOutcome.CLEAN,
            SecurityPolicyVersion = AssetProcessingDefaults.MALWARE_POLICY_VERSION,
            EngineIdentity = "clamav",
            EngineVersion = "1.0.3",
            SignatureIdentity = "clamav 1.0.3/25425",
            SignatureObservedAt = signatureObservedAt ?? now.AddMinutes(-1),
            SignatureValidUntil = signatureValidUntil ?? now.AddDays(2),
            IdentityAvailable = true,
            ScannedByteCount = _content.Length,
            ScopeComplete = true,
            CompletedAt = now,
            EvidenceDigest = NewEvidenceDigest(),
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        return new SeededProof(asset.Id, version.Id, archiveId, malwareId, chain.PolicyDigest);
    }

    private static AssetProcessingJob ProducerJob(
        Guid assetId,
        Guid assetVersionId,
        AssetProcessingJobType type,
        string payload,
        Guid? securityRecheckId,
        DateTimeOffset now) =>
        new()
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            Type = type,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.SUCCEEDED,
            Stage = "READY",
            AttemptCount = 0,
            MaxAttempts = 3,
            AvailableAt = now,
            StartedAt = now,
            CompletedAt = now,
            Payload = payload,
            SecurityRecheckId = securityRecheckId,
            CreatedAt = now,
            UpdatedAt = now
        };

    /// <summary>Creates the selected SUCCEEDED generation-2 proof a completed recheck would leave.</summary>
    private static async Task<(Guid RecheckId, Guid ArchiveEvidenceId, Guid MalwareEvidenceId)> SeedSelectedRecheck(
        ApplicationDbContext db,
        SeededProof seed)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var recheckId = Guid.NewGuid();
        var archiveId = Guid.NewGuid();
        var malwareId = Guid.NewGuid();

        db.SecurityRechecks.Add(new SecurityRecheck
        {
            Id = recheckId,
            AssetId = seed.AssetId,
            AssetVersionId = seed.VersionId,
            ContentSha256 = _contentSha256,
            SecurityGeneration = 2,
            SecurityPolicyVersion = "policy-v1",
            DefinitionVersion = 1,
            OperationDigest = new string('b', 64),
            OperationReceiptId = Guid.NewGuid(),
            State = SecurityRecheckState.SUCCEEDED,
            SelectedGeneration = 2,
            CompletedGeneration = 2,
            RequestedAt = now,
            CompletedAt = now
        });

        // The recheck row and its evidence reference each other, so they commit in two steps.
        await db.SaveChangesAsync();

        // A completed recheck leaves its own succeeded scoped producers behind, and the scanner
        // one carries exactly the policy version the approved recheck pinned.
        AssetProcessingJob archiveJob = ProducerJob(
            seed.AssetId, seed.VersionId, AssetProcessingJobType.ARCHIVE_INSPECTION,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.ARCHIVE_INSPECTION, new ArchiveInspectionPayload()),
            recheckId, now);
        AssetProcessingJob malwareJob = ProducerJob(
            seed.AssetId, seed.VersionId, AssetProcessingJobType.MALWARE_SCAN,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.MALWARE_SCAN, new MalwareScanPayload("policy-v1")),
            recheckId, now);
        db.AssetProcessingJobs.AddRange(archiveJob, malwareJob);

        await db.SecurityEvidenceRecords.AddRangeAsync(
            new SecurityEvidenceRecord
            {
                Id = archiveId,
                AssetId = seed.AssetId,
                AssetVersionId = seed.VersionId,
                ContentSha256 = _contentSha256,
                SecurityRecheckId = recheckId,
                JobId = archiveJob.Id,
                EvidenceKind = SecurityEvidenceKind.ARCHIVE_VALIDATION,
                Generation = 2,
                DefinitionVersion = 1,
                Outcome = SecurityEvidenceOutcome.VALID,
                SecurityPolicyVersion = "policy-v1",
                ImplementationIdentity = "archive-inspector",
                IdentityAvailable = false,
                ScannedByteCount = _content.Length,
                ScannedEntryCount = 3,
                ScopeComplete = true,
                CompletedAt = now,
                EvidenceDigest = NewEvidenceDigest(),
                CreatedAt = now
            },
            new SecurityEvidenceRecord
            {
                Id = malwareId,
                AssetId = seed.AssetId,
                AssetVersionId = seed.VersionId,
                ContentSha256 = _contentSha256,
                SecurityRecheckId = recheckId,
                JobId = malwareJob.Id,
                EvidenceKind = SecurityEvidenceKind.MALWARE_SCAN,
                Generation = 2,
                DefinitionVersion = 1,
                Outcome = SecurityEvidenceOutcome.CLEAN,
                SecurityPolicyVersion = "policy-v1",
                EngineIdentity = "clamav",
                EngineVersion = "1.0.3",
                SignatureIdentity = "clamav 1.0.3/25425",
                SignatureObservedAt = now.AddMinutes(-1),
                SignatureValidUntil = now.AddDays(2),
                IdentityAvailable = true,
                ScannedByteCount = _content.Length,
                ScopeComplete = true,
                CompletedAt = now,
                EvidenceDigest = NewEvidenceDigest(),
                CreatedAt = now
            });
        await db.SaveChangesAsync();

        return (recheckId, archiveId, malwareId);
    }
}
