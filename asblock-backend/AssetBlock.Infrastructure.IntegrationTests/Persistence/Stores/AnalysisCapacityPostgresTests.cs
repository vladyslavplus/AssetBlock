using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Focused PostgreSQL proof for the shared analysis capacity: the general queue must never claim
/// analysis work, waiting must not consume attempts, one reservation serves one execution, and a
/// refund or recovery only ever touches the exact reservation identity it was given.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class AnalysisCapacityPostgresTests(PostgresFixture fixture)
{
    private const string SERVICE_KEY = "code-analysis";

    [Fact]
    public async Task ClaimPendingBatch_WhenAnalysisJobQueued_ShouldNeverReturnIt()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);

        IReadOnlyList<ClaimedAssetProcessingJob> claimed = await seed.JobStore.ClaimPendingBatch(
            batchSize: 10,
            leaseDuration: TimeSpan.FromMinutes(5),
            leaseOwner: "general-worker");

        claimed.Should().NotContain(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS);

        AssetProcessingJob analysis = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == seed.AnalysisJobId);
        analysis.Status.Should().Be(AssetProcessingJobStatus.QUEUED);
        analysis.AttemptCount.Should().Be(0);
    }

    [Fact]
    public async Task ClaimWithCapacity_WhenSlotFree_ShouldReserveAndBindRunJobAndSlot()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        IReadOnlyList<ClaimedAssetProcessingJob> claimed = await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker");

        claimed.Should().ContainSingle();
        ClaimedAssetProcessingJob job = claimed[0];
        job.Type.Should().Be(AssetProcessingJobType.CODE_ANALYSIS);
        job.AttemptCount.Should().Be(1);

        // The transport binding is an explicit 64 hex value, never the job lease Guid.
        job.TransportLeaseToken.Should().NotBeNull();
        job.TransportLeaseToken.Should().HaveLength(64);
        job.TransportLeaseToken.Should().MatchRegex("^[0-9a-f]{64}$");
        job.TransportLeaseToken.Should().NotBe(job.LeaseToken.ToString());

        AnalysisCapacitySlot slot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        slot.State.Should().Be(AnalysisCapacitySlotState.RESERVED);
        slot.ReservedJobId.Should().Be(job.JobId);
        slot.ReservedRunId.Should().Be(seed.AnalysisRunId);
        slot.HardDeadlineUtc.Should().NotBeNull();

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.AnalysisRunId);
        run.State.Should().Be(CodeAnalysisRunState.RUNNING);
        run.TransportLeaseToken.Should().Be(job.TransportLeaseToken);
        run.JobId.Should().Be(job.JobId);
    }

    [Fact]
    public async Task ClaimWithCapacity_WhenSlotAlreadyReserved_ShouldNotClaimOrConsumeAttempts()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a"))
            .Should().ContainSingle();

        IReadOnlyList<ClaimedAssetProcessingJob> second = await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "worker-b");

        // A second worker must not steal the single execution while it is still reserved.
        second.Should().BeEmpty();

        AssetProcessingJob analysis = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == seed.AnalysisJobId);
        analysis.AttemptCount.Should().Be(1);
    }

    [Fact]
    public async Task ClaimWithCapacity_WhenTwoWorkersOverlapAtCapacityOne_ShouldClaimExactlyOnce()
    {
        await using ApplicationDbContext setupDb = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(setupDb);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        const int advisoryClass = 23117;
        const int advisoryKey = 412;
        await setupDb.Database.ExecuteSqlRawAsync($"""
            CREATE OR REPLACE FUNCTION round12_pause_analysis_claim() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN
                IF OLD."State" = 'FREE' AND NEW."State" = 'RESERVED' THEN
                    PERFORM pg_advisory_xact_lock({advisoryClass}, {advisoryKey});
                END IF;
                RETURN NEW;
            END
            $$;
            CREATE TRIGGER round12_pause_analysis_claim
            BEFORE UPDATE ON analysis_capacity_slots
            FOR EACH ROW EXECUTE FUNCTION round12_pause_analysis_claim();
            """);

        try
        {
            await using ApplicationDbContext barrierDb = fixture.CreateDbContext();
            await barrierDb.Database.OpenConnectionAsync();
            await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction barrierTransaction =
                await barrierDb.Database.BeginTransactionAsync();
            await barrierDb.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({advisoryClass}, {advisoryKey})");

            await using ApplicationDbContext firstDb = fixture.CreateDbContext();
            await firstDb.Database.OpenConnectionAsync();
            var firstPid = await firstDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
            IAnalysisCapacityStore firstCapacity = CreateCapacity(firstDb);
            Task<IReadOnlyList<ClaimedAssetProcessingJob>> firstClaim = firstCapacity.ClaimWithCapacity(
                1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a");

            await WaitForLockWait(barrierDb, firstPid);

            await using ApplicationDbContext secondDb = fixture.CreateDbContext();
            IAnalysisCapacityStore secondCapacity = CreateCapacity(secondDb);
            (await secondCapacity.ClaimWithCapacity(
                1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-b"))
                .Should().BeEmpty();

            await barrierTransaction.CommitAsync();
            (await firstClaim).Should().ContainSingle();

            AssetProcessingJob job = await setupDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == seed.AnalysisJobId);
            job.AttemptCount.Should().Be(1);
            job.Status.Should().Be(AssetProcessingJobStatus.RUNNING);
            (await setupDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State
                .Should().Be(AnalysisCapacitySlotState.RESERVED);
        }
        finally
        {
            await setupDb.Database.ExecuteSqlRawAsync("DROP TRIGGER IF EXISTS round12_pause_analysis_claim ON analysis_capacity_slots");
            await setupDb.Database.ExecuteSqlRawAsync("DROP FUNCTION IF EXISTS round12_pause_analysis_claim()");
        }
    }

    [Fact]
    public async Task DeleteOverlapsExpiredReservationRecovery_WithoutDeadlockOrBreakingReservation()
    {
        await using ApplicationDbContext setupDb = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(setupDb);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-delete-race")).Single();
        await setupDb.AnalysisCapacitySlots
            .Where(s => s.ReservedRunId == seed.AnalysisRunId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CleanupGraceUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));

        await using ApplicationDbContext holder = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction holderTx =
            await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM asset_processing_jobs WHERE \"Id\" = {claimed.JobId} FOR UPDATE");

        await using ApplicationDbContext deleteDb = fixture.CreateDbContext();
        await deleteDb.Database.OpenConnectionAsync();
        var deletePid = await deleteDb.Database.SqlQueryRaw<int>(
            "SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        Task deleteTask = new AssetStore(deleteDb).Delete(seed.AssetId);
        await WaitForLockWait(setupDb, deletePid);

        await using ApplicationDbContext recoveryDb = fixture.CreateDbContext();
        await recoveryDb.Database.OpenConnectionAsync();
        Task<int> recoveryTask = CreateCapacity(recoveryDb).RecoverExpiredReservations();
        // Recovery uses SKIP LOCKED, so its forced overlap is a completed zero-work pass while
        // deletion owns the service slot and is visibly waiting on the held job row.
        (await recoveryTask.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be(0);

        await holderTx.CommitAsync();
        Func<Task> deleteOperation = () => deleteTask;
        await deleteOperation.Should().ThrowAsync<PostgresException>()
            .Where(ex => (ex.SqlState == PostgresErrorCodes.ForeignKeyViolation
                || ex.SqlState == PostgresErrorCodes.RestrictViolation)
                && ex.ConstraintName == "FK_code_analysis_runs_assets_AssetId");
        (await CreateCapacity(recoveryDb).RecoverExpiredReservations()).Should().Be(1);

        (await setupDb.Assets.AsNoTracking().AnyAsync(a => a.Id == seed.AssetId)).Should().BeTrue();
        (await setupDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.AnalysisRunId)).State
            .Should().Be(CodeAnalysisRunState.QUEUED);
        (await setupDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId)).Status
            .Should().Be(AssetProcessingJobStatus.RETRY_SCHEDULED);
        AnalysisCapacitySlot recovered = await setupDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        recovered.State.Should().Be(AnalysisCapacitySlotState.FREE);
        recovered.ReleasedReason.Should().Be(AnalysisReleaseReasons.DEADLINE_RECONCILED);
    }

    [Fact]
    public async Task EnsureSlots_WhenCapacityDiffersFromRegistration_ShouldFailClosed()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        Func<Task> act = () => seed.Capacity.EnsureSlots(SERVICE_KEY, 2);

        await act.Should().ThrowAsync<InvalidOperationException>();

        // No extra slot may appear from a rejected configuration.
        (await db.AnalysisCapacitySlots.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RefundNotStartedClaim_WhenLiveClaimRefused_ShouldRefundExactlyOneAttempt()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();

        AnalysisCapacitySlot slot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        var generation = (int)slot.ReservationGeneration!;

        (await seed.Capacity.RefundNotStartedClaim(
            job.JobId, job.LeaseToken, job.TransportLeaseToken!, generation)).Should().BeTrue();

        AssetProcessingJob refunded = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == job.JobId);
        refunded.AttemptCount.Should().Be(0);
        refunded.Status.Should().Be(AssetProcessingJobStatus.QUEUED);
        refunded.Stage.Should().Be("BUSY_NOT_STARTED");

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.AnalysisRunId);
        run.State.Should().Be(CodeAnalysisRunState.QUEUED);
        run.TransportLeaseToken.Should().BeNull();

        (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State.Should().Be(AnalysisCapacitySlotState.FREE);
    }

    [Fact]
    public async Task RefundNotStartedClaim_WhenWrongReservationTokenOrStaleLease_ShouldNotMutate()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();

        AnalysisCapacitySlot slot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        var generation = (int)slot.ReservationGeneration!;

        // Wrong transport token.
        (await seed.Capacity.RefundNotStartedClaim(job.JobId, job.LeaseToken, new string('a', 64), generation))
            .Should().BeFalse();

        // Wrong reservation generation.
        (await seed.Capacity.RefundNotStartedClaim(job.JobId, job.LeaseToken, job.TransportLeaseToken!, generation + 99))
            .Should().BeFalse();

        // Wrong job lease.
        (await seed.Capacity.RefundNotStartedClaim(job.JobId, Guid.NewGuid(), job.TransportLeaseToken!, generation))
            .Should().BeFalse();

        AssetProcessingJob untouched = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == job.JobId);
        untouched.AttemptCount.Should().Be(1);
        untouched.Status.Should().Be(AssetProcessingJobStatus.RUNNING);

        (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State.Should().Be(AnalysisCapacitySlotState.RESERVED);
    }

    [Fact]
    public async Task ReleaseReservation_WhenJobStillRunningAndNoTermination_ShouldNotFreeSlot()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();

        Guid slotId = (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).Id;

        // A database status is not evidence that the external execution stopped.
        (await seed.Capacity.ReleaseReservation(
            slotId, job.TransportLeaseToken!, AnalysisExecutionDisposition.UNCONFIRMED)).Should().BeFalse();

        (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State.Should().Be(AnalysisCapacitySlotState.RESERVED);
    }

    [Fact]
    public async Task ReleaseReservation_WhenJobSucceededAndExecutionConfirmed_ShouldFreeSlotWithTruthfulReason()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();

        await SetTerminalJobState(db, job.JobId, AssetProcessingJobStatus.SUCCEEDED);

        Guid slotId = (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).Id;

        (await seed.Capacity.ReleaseReservation(
            slotId, job.TransportLeaseToken!, AnalysisExecutionDisposition.CONFIRMED_TERMINATED)).Should().BeTrue();

        AnalysisCapacitySlot slot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        slot.State.Should().Be(AnalysisCapacitySlotState.FREE);
        slot.ReleasedReason.Should().Be(AnalysisReleaseReasons.COMPLETED);
        slot.ConfirmedTerminationAt.Should().NotBeNull();
        slot.ReservedRunId.Should().BeNull();
        slot.ReservedJobId.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseReservation_WhenJobFailedAndOutcomeUnconfirmed_ShouldKeepSlotFenced()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();

        await SetTerminalJobState(db, job.JobId, AssetProcessingJobStatus.FAILED, "ANALYSIS_RESPONSE_INVALID");

        Guid slotId = (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).Id;

        // A terminal job is a fact about the job, not proof that an accepted external execution
        // stopped, so it cannot release an unconfirmed reservation before the enforced deadline.
        (await seed.Capacity.ReleaseReservation(
            slotId, job.TransportLeaseToken!, AnalysisExecutionDisposition.UNCONFIRMED)).Should().BeFalse();

        AnalysisCapacitySlot slot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        slot.State.Should().Be(AnalysisCapacitySlotState.RESERVED);
        slot.ReleasedReason.Should().BeNull();
        slot.ConfirmedTerminationAt.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseReservation_WhenRefusedBeforeAcceptance_ShouldFreeSlotWithoutTerminationTimestamp()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();

        Guid slotId = (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).Id;

        (await seed.Capacity.ReleaseReservation(
            slotId, job.TransportLeaseToken!, AnalysisExecutionDisposition.NOT_STARTED)).Should().BeTrue();

        AnalysisCapacitySlot slot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        slot.State.Should().Be(AnalysisCapacitySlotState.FREE);
        slot.ReleasedReason.Should().Be(AnalysisReleaseReasons.NOT_STARTED_REFUND);

        // Nothing executed, so there is no termination to confirm.
        slot.ConfirmedTerminationAt.Should().BeNull();
    }

    [Fact]
    public async Task ReleaseReservation_WhenJobFailedAndOutcomeUnconfirmedAfterDeadline_ShouldReconcile()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();

        await SetTerminalJobState(db, job.JobId, AssetProcessingJobStatus.FAILED, "ANALYSIS_RESPONSE_INVALID");
        await ExpireReservation(db);

        Guid slotId = (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).Id;

        (await seed.Capacity.ReleaseReservation(
            slotId, job.TransportLeaseToken!, AnalysisExecutionDisposition.UNCONFIRMED)).Should().BeTrue();

        AnalysisCapacitySlot slot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        slot.State.Should().Be(AnalysisCapacitySlotState.FREE);
        slot.ReleasedReason.Should().Be(AnalysisReleaseReasons.DEADLINE_RECONCILED);

        // Reconciliation fences the slot, it does not claim a confirmed termination.
        slot.ConfirmedTerminationAt.Should().BeNull();
    }

    /// <summary>Moves the reservation's cleanup grace into the past so the deadline fence is enforced.</summary>
    private static async Task ExpireReservation(ApplicationDbContext db)
    {
        AnalysisCapacitySlot reserved = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        await db.AnalysisCapacitySlots
            .Where(s => s.Id == reserved.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CleanupGraceUntil, DateTimeOffset.UtcNow.AddHours(-1)));
    }

    [Fact]
    public async Task ReleaseReservation_WhenCalledTwice_ShouldBeIdempotent()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();

        await SetTerminalJobState(db, job.JobId, AssetProcessingJobStatus.SUCCEEDED);

        Guid slotId = (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).Id;

        (await seed.Capacity.ReleaseReservation(
            slotId, job.TransportLeaseToken!, AnalysisExecutionDisposition.CONFIRMED_TERMINATED)).Should().BeTrue();

        // The second call must not re-free or fabricate a new confirmation.
        (await seed.Capacity.ReleaseReservation(
            slotId, job.TransportLeaseToken!, AnalysisExecutionDisposition.CONFIRMED_TERMINATED)).Should().BeFalse();

        AnalysisCapacitySlot slot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        slot.State.Should().Be(AnalysisCapacitySlotState.FREE);
        slot.ReleasedReason.Should().Be(AnalysisReleaseReasons.COMPLETED);
    }

    [Fact]
    public async Task ReleaseForTerminalJob_WhenReservationAlreadyReplaced_ShouldNotFreeNewReservation()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();

        // A stale token from the previous reservation must not release the current one.
        (await seed.Capacity.ReleaseForTerminalJob(
            job.JobId, new string('b', 64), AnalysisExecutionDisposition.CONFIRMED_TERMINATED)).Should().BeFalse();

        (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State.Should().Be(AnalysisCapacitySlotState.RESERVED);
    }

    [Fact]
    public async Task RenewAnalysisLease_WhenJobNotClaimed_ShouldReturnFalse()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        (await seed.Capacity.RenewAnalysisLease(seed.AnalysisJobId, Guid.NewGuid(), TimeSpan.FromMinutes(5)))
            .Should().BeFalse();
    }

    [Fact]
    public async Task RecoverExpiredReservations_WhenGraceNotElapsed_ShouldLeaveReservationIntact()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a");

        (await seed.Capacity.RecoverExpiredReservations()).Should().Be(0);
        (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State.Should().Be(AnalysisCapacitySlotState.RESERVED);
    }

    [Fact]
    public async Task RecoverExpiredReservations_WhenGraceElapsed_ShouldReconcileOnlyThatReservation()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);

        ClaimedAssetProcessingJob job = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();

        AnalysisCapacitySlot slot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        await db.AnalysisCapacitySlots
            .Where(s => s.Id == slot.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CleanupGraceUntil, DateTimeOffset.UtcNow.AddHours(-1)));

        (await seed.Capacity.RecoverExpiredReservations()).Should().Be(1);

        AnalysisCapacitySlot recovered = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        recovered.State.Should().Be(AnalysisCapacitySlotState.FREE);
        recovered.ReservedRunId.Should().BeNull();
        recovered.ReservedJobId.Should().BeNull();

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.AnalysisRunId);
        run.State.Should().Be(CodeAnalysisRunState.QUEUED);
        run.TransportLeaseToken.Should().BeNull();

        AssetProcessingJob recoveredJob = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == job.JobId);
        recoveredJob.Status.Should().Be(AssetProcessingJobStatus.RETRY_SCHEDULED);
    }

    /// <summary>
    /// Moves a claimed job to a terminal state the way a real transition would: lease fields cleared
    /// and a completion timestamp written, so the job table constraints stay satisfied.
    /// </summary>
    private static Task SetTerminalJobState(
        ApplicationDbContext db,
        Guid jobId,
        AssetProcessingJobStatus status,
        string? errorCode = null)
    {
        return db.AssetProcessingJobs
            .Where(j => j.Id == jobId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, status)
                .SetProperty(j => j.LeaseOwner, (string?)null)
                .SetProperty(j => j.LeaseToken, (Guid?)null)
                .SetProperty(j => j.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(j => j.TransportLeaseToken, (string?)null)
                .SetProperty(j => j.ErrorCode, errorCode)
                .SetProperty(j => j.CompletedAt, DateTimeOffset.UtcNow));
    }

    private static IAnalysisCapacityStore CreateCapacity(ApplicationDbContext db) => new AnalysisCapacityStore(
        db,
        MsOptions.Create(new CodeAnalysisOptions { Enabled = true, ServiceKey = SERVICE_KEY, Capacity = 1 }),
        NullLogger<AnalysisCapacityStore>.Instance);

    private static async Task WaitForLockWait(ApplicationDbContext observer, int pid)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var waiting = await observer.Database.SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM pg_stat_activity WHERE pid = {0} AND wait_event_type = 'Lock'", pid)
                .SingleAsync();
            if (waiting == 1)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"PostgreSQL backend {pid} did not reach the expected lock barrier.");
    }
    private sealed record SeedData(
        ApplicationDbContext Db,
        IAssetProcessingJobStore JobStore,
        IAnalysisCapacityStore Capacity,
        Guid AssetId,
        Guid AssetVersionId,
        Guid AnalysisRunId,
        Guid AnalysisJobId);

    private static async Task<SeedData> Seed(ApplicationDbContext db)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Capacity subject");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        await AdmittedReportTestEvidence.SeedObservedProofAsync(db, version, chain);

        var runId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var inputDigest = AdmittedReportTestEvidence.NewHex64();

        db.CodeAnalysisRuns.Add(new CodeAnalysisRun
        {
            Id = runId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            PolicyVersionId = chain.PolicyVersionId,
            PolicyDigest = chain.PolicyDigest,
            DeclarationDigest = AdmittedReportTestEvidence.NewHex64(),
            MaterialDigest = AdmittedReportTestEvidence.NewHex64(),
            InputDigest = inputDigest,
            SelectionGeneration = 1,
            JobId = null,
            ArchiveEvidenceId = chain.ArchiveEvidenceId,
            MalwareEvidenceId = chain.MalwareEvidenceId,
            SecurityGeneration = 1,
            ServiceKey = SERVICE_KEY,
            State = CodeAnalysisRunState.QUEUED,
            CreatedAt = DateTimeOffset.UtcNow
        });

        // The run must exist before the job that references it, exactly as the enqueuer writes it.
        await db.SaveChangesAsync();

        db.AssetProcessingJobs.Add(new AssetProcessingJob
        {
            Id = jobId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            Type = AssetProcessingJobType.CODE_ANALYSIS,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.QUEUED,
            Stage = "QUEUED",
            AttemptCount = 0,
            MaxAttempts = 3,
            AvailableAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            AnalysisRunId = runId,
            Payload = AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.CODE_ANALYSIS,
                new CodeAnalysisPayload(
                    runId, inputDigest, chain.PolicyDigest, version.ContentSha256,
                    chain.ArchiveEvidenceId, chain.MalwareEvidenceId, 1))
        });

        await db.SaveChangesAsync();

        await db.CodeAnalysisRuns
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, jobId));

        db.ChangeTracker.Clear();

        IAssetProcessingJobStore jobStore = new AssetProcessingJobStore(
            db, NullLogger<AssetProcessingJobStore>.Instance, MsOptions.Create(new AssetProcessingOptions()));

        IAnalysisCapacityStore capacity = new AnalysisCapacityStore(
            db,
            MsOptions.Create(new CodeAnalysisOptions { Enabled = true, ServiceKey = SERVICE_KEY, Capacity = 1 }),
            NullLogger<AnalysisCapacityStore>.Instance);

        return new SeedData(db, jobStore, capacity, asset.Id, version.Id, runId, jobId);
    }
}
