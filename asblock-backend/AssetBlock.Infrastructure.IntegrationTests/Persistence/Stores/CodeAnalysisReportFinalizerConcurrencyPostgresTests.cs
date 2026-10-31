using System.Data.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;
namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class CodeAnalysisReportFinalizerConcurrencyPostgresTests(PostgresFixture fixture)
{
    private const string SERVICE_KEY = "code-analysis";
    private const string POLICY_KEY = "code-analysis-policy-v1";

    [Fact]
    public async Task FinalizeConfirmed_WhenTwoIndependentContextsResolveSameBody_ShouldProduceOneEffectsSet()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();

        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.Chain.PolicyDigest);

        await using ApplicationDbContext firstDb = fixture.CreateDbContext();
        await using ApplicationDbContext secondDb = fixture.CreateDbContext();
        ICodeAnalysisReportFinalizer firstFinalizer = new CodeAnalysisReportFinalizer(firstDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        ICodeAnalysisReportFinalizer secondFinalizer = new CodeAnalysisReportFinalizer(secondDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);

        Task<AssetProcessingJobOutcome> firstOutcome = firstFinalizer.FinalizeConfirmed(claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        Task<AssetProcessingJobOutcome> secondOutcome = secondFinalizer.FinalizeConfirmed(claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        await Task.WhenAll(firstOutcome, secondOutcome);

        (await firstOutcome).Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        (await secondOutcome).Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        (await seedDb.CodeAnalysisReportHeaders.AsNoTracking().Where(h => h.AnalysisRunId == seed.RunId).CountAsync()).Should().Be(1);
        (await seedDb.CodeAnalysisReportStages.AsNoTracking().CountAsync()).Should().Be(8);
        (await seedDb.CodeAnalysisReportFindings.AsNoTracking().CountAsync()).Should().Be(1);
        (await seedDb.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync()).Should().Be(1);
        (await seedDb.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "CODE_ANALYSIS_FINALIZED")).Should().Be(1);
        (await seedDb.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenFaultOccursAfterLateInsert_ShouldRollbackEveryEffect()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();

        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.Chain.PolicyDigest);

        var fault = new LateAuditFaultInterceptor();
        await using ApplicationDbContext faultDb = fixture.CreateDbContext(o => o.AddInterceptors(fault));
        ICodeAnalysisReportFinalizer faultFinalizer = new CodeAnalysisReportFinalizer(
            faultDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);

        Func<Task> act = () => faultFinalizer.FinalizeConfirmed(claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using ApplicationDbContext verifierDb = fixture.CreateDbContext();
        (await verifierDb.CodeAnalysisReportHeaders.AsNoTracking().CountAsync(h => h.AnalysisRunId == seed.RunId)).Should().Be(0);
        (await verifierDb.CodeAnalysisReportStages.AsNoTracking().CountAsync()).Should().Be(0);
        (await verifierDb.CodeAnalysisReportFindings.AsNoTracking().CountAsync()).Should().Be(0);
        (await verifierDb.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync()).Should().Be(0);
        (await verifierDb.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "CODE_ANALYSIS_FINALIZED")).Should().Be(0);
        (await verifierDb.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);
        (await verifierDb.UserNotifications.AsNoTracking().CountAsync(n => n.RecipientUserId == seed.Asset.AuthorId)).Should().Be(0);
        (await verifierDb.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SelectedCodeAnalysisReportId.Should().BeNull();

        await using ApplicationDbContext retryDb = fixture.CreateDbContext();
        ICodeAnalysisReportFinalizer retryFinalizer = new CodeAnalysisReportFinalizer(
            retryDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        AssetProcessingJobOutcome retry = await retryFinalizer.FinalizeConfirmed(claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        retry.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>().Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenAssetLockedAndLeaseExpired_ShouldRejectNewWrites()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();

        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.Chain.PolicyDigest);

        await using ApplicationDbContext holder = fixture.CreateDbContext();
        await using IDbContextTransaction holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM assets WHERE \"Id\" = {seed.Asset.Id} FOR UPDATE");

        await using ApplicationDbContext finalizerDb = fixture.CreateDbContext();
        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            finalizerDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        Task<AssetProcessingJobOutcome> task = finalizer.FinalizeConfirmed(claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);

        // Deterministic barrier: the overlapped finalizer must be visibly waiting on the held
        // Asset row lock before the lease is expired. A fixed sleep alone cannot prove this.
        await WaitForAssetRowLockWaiter();
        task.IsCompleted.Should().BeFalse();

        await using (ApplicationDbContext expiryDb = fixture.CreateDbContext())
        {
            await expiryDb.AssetProcessingJobs.Where(j => j.Id == claimed.JobId).ExecuteUpdateAsync(s =>
                s.SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)));
        }

        await holderTransaction.CommitAsync();
        AssetProcessingJobOutcome rejected = await task;
        rejected.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>();

        await using ApplicationDbContext verifierDb = fixture.CreateDbContext();
        (await verifierDb.CodeAnalysisReportHeaders.AsNoTracking().CountAsync(h => h.AnalysisRunId == seed.RunId)).Should().Be(0);
        (await verifierDb.CodeAnalysisReportStages.AsNoTracking().CountAsync()).Should().Be(0);
        (await verifierDb.CodeAnalysisReportFindings.AsNoTracking().CountAsync()).Should().Be(0);
        (await verifierDb.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync()).Should().Be(0);
        (await verifierDb.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "CODE_ANALYSIS_FINALIZED")).Should().Be(0);
        (await verifierDb.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);
        (await verifierDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId)).State
            .Should().Be(CodeAnalysisRunState.RUNNING);
        (await verifierDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId)).Status
            .Should().Be(AssetProcessingJobStatus.RUNNING);
    }

    [Fact]
    public async Task FinalizeConfirmed_OverlappingExpiredReservationRecovery_ShouldSerializeWithoutDeadlock()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "capacity-worker")).Single();

        await seedDb.AnalysisCapacitySlots
            .Where(s => s.ReservedRunId == seed.RunId)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CleanupGraceUntil, DateTimeOffset.UtcNow.AddMinutes(-1)));
        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.Chain.PolicyDigest);

        // Hold the job row. The finalizer reaches the run/job part of its lock sequence and waits;
        // recovery then owns the slot and waits for that same run. Releasing the holder must allow
        // the run-before-job order to finish finalization and let recovery observe the committed
        // terminal state, without losing or rewriting the report.
        await using ApplicationDbContext holder = fixture.CreateDbContext();
        await using IDbContextTransaction holderTx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM asset_processing_jobs WHERE \"Id\" = {claimed.JobId} FOR UPDATE");

        await using ApplicationDbContext finalizerDb = fixture.CreateDbContext();
        await finalizerDb.Database.OpenConnectionAsync();
        var finalizerPid = await finalizerDb.Database.SqlQueryRaw<int>(
            "SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            finalizerDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        Task<AssetProcessingJobOutcome> finalizeTask = finalizer.FinalizeConfirmed(
            claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        await WaitForBackendLockWait(finalizerPid);

        await using ApplicationDbContext recoveryDb = fixture.CreateDbContext();
        await recoveryDb.Database.OpenConnectionAsync();
        var recoveryPid = await recoveryDb.Database.SqlQueryRaw<int>(
            "SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        IAnalysisCapacityStore recovery = new AnalysisCapacityStore(
            recoveryDb,
            MsOptions.Create(new CodeAnalysisOptions { Enabled = true, ServiceKey = SERVICE_KEY, Capacity = 1 }),
            NullLogger<AnalysisCapacityStore>.Instance);
        Task<int> recoveryTask = recovery.RecoverExpiredReservations();
        await WaitForBackendLockWait(recoveryPid);

        await holderTx.CommitAsync();
        await Task.WhenAll(finalizeTask, recoveryTask).WaitAsync(TimeSpan.FromSeconds(20));
        (await finalizeTask).Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        (await recoveryTask).Should().Be(1);

        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        (await verifyDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId)).State
            .Should().Be(CodeAnalysisRunState.COMPLETED);
        (await verifyDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId)).Status
            .Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        (await verifyDb.CodeAnalysisReportHeaders.AsNoTracking().CountAsync(h => h.AnalysisRunId == seed.RunId))
            .Should().Be(1);
        AnalysisCapacitySlot reconciled = await verifyDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        reconciled.State.Should().Be(AnalysisCapacitySlotState.FREE);
        reconciled.ReleasedReason.Should().Be(AnalysisReleaseReasons.DEADLINE_RECONCILED);
    }

    [Fact]
    public async Task FinalizeConfirmed_OverlappingLeaseRenewal_ShouldNotRenewACompletedClaim()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "capacity-worker")).Single();
        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.Chain.PolicyDigest);

        await using ApplicationDbContext holder = fixture.CreateDbContext();
        await using IDbContextTransaction holderTx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM asset_processing_jobs WHERE \"Id\" = {claimed.JobId} FOR UPDATE");

        await using ApplicationDbContext finalizerDb = fixture.CreateDbContext();
        await finalizerDb.Database.OpenConnectionAsync();
        var finalizerPid = await finalizerDb.Database.SqlQueryRaw<int>(
            "SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            finalizerDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        Task<AssetProcessingJobOutcome> finalizeTask = finalizer.FinalizeConfirmed(
            claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        await WaitForBackendLockWait(finalizerPid);

        await using ApplicationDbContext renewDb = fixture.CreateDbContext();
        await renewDb.Database.OpenConnectionAsync();
        var renewPid = await renewDb.Database.SqlQueryRaw<int>(
            "SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        IAnalysisCapacityStore capacity = new AnalysisCapacityStore(
            renewDb,
            MsOptions.Create(new CodeAnalysisOptions { Enabled = true, ServiceKey = SERVICE_KEY, Capacity = 1 }),
            NullLogger<AnalysisCapacityStore>.Instance);
        Task<bool> renewTask = capacity.RenewAnalysisLease(
            claimed.JobId, claimed.LeaseToken, TimeSpan.FromMinutes(5));
        await WaitForBackendLockWait(renewPid);

        await holderTx.CommitAsync();
        await Task.WhenAll(finalizeTask, renewTask).WaitAsync(TimeSpan.FromSeconds(20));
        (await finalizeTask).Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        (await renewTask).Should().BeFalse();

        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        CodeAnalysisRun completedRun = await verifyDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        completedRun.State.Should().Be(CodeAnalysisRunState.COMPLETED);
        AssetProcessingJob completedJob = await verifyDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId);
        completedJob.Status.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        completedJob.AttemptCount.Should().Be(1);
        (await verifyDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State
            .Should().Be(AnalysisCapacitySlotState.RESERVED);
    }

    [Fact]
    public async Task FinalizeConfirmed_OverlappingNotStartedRefund_ShouldNotRefundAnAcceptedClaim()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "capacity-worker")).Single();
        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.Chain.PolicyDigest);
        var generation = (await seedDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).ReservationGeneration!.Value;

        await using ApplicationDbContext holder = fixture.CreateDbContext();
        await using IDbContextTransaction holderTx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT \"Id\" FROM asset_processing_jobs WHERE \"Id\" = {claimed.JobId} FOR UPDATE");

        await using ApplicationDbContext finalizerDb = fixture.CreateDbContext();
        await finalizerDb.Database.OpenConnectionAsync();
        var finalizerPid = await finalizerDb.Database.SqlQueryRaw<int>(
            "SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            finalizerDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        Task<AssetProcessingJobOutcome> finalizeTask = finalizer.FinalizeConfirmed(
            claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        await WaitForBackendLockWait(finalizerPid);

        await using ApplicationDbContext refundDb = fixture.CreateDbContext();
        await refundDb.Database.OpenConnectionAsync();
        var refundPid = await refundDb.Database.SqlQueryRaw<int>(
            "SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        IAnalysisCapacityStore capacity = new AnalysisCapacityStore(
            refundDb,
            MsOptions.Create(new CodeAnalysisOptions { Enabled = true, ServiceKey = SERVICE_KEY, Capacity = 1 }),
            NullLogger<AnalysisCapacityStore>.Instance);
        Task<bool> refundTask = capacity.RefundNotStartedClaim(
            claimed.JobId, claimed.LeaseToken, claimed.TransportLeaseToken!, generation);
        await WaitForBackendLockWait(refundPid);

        await holderTx.CommitAsync();
        await Task.WhenAll(finalizeTask, refundTask).WaitAsync(TimeSpan.FromSeconds(20));
        (await finalizeTask).Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        (await refundTask).Should().BeFalse();

        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        CodeAnalysisRun completedRun = await verifyDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        completedRun.State.Should().Be(CodeAnalysisRunState.COMPLETED);
        AssetProcessingJob completedJob = await verifyDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId);
        completedJob.Status.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        completedJob.AttemptCount.Should().Be(1);
        (await verifyDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State
            .Should().Be(AnalysisCapacitySlotState.RESERVED);
    }

    [Fact]
    public async Task QuarantineIntegrity_WhenAssetLockedAndLeaseExpired_ShouldReject()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();

        await using ApplicationDbContext holder = fixture.CreateDbContext();
        await using IDbContextTransaction holderTransaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT \"Id\" FROM assets WHERE \"Id\" = {seed.Asset.Id} FOR UPDATE");

        await using ApplicationDbContext quarantinerDb = fixture.CreateDbContext();
        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            quarantinerDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        Task<AssetProcessingJobOutcome> task = finalizer.QuarantineIntegrity(claimed.JobId, claimed.LeaseToken, "ARCHIVE_CORRUPT", CancellationToken.None);

        // Deterministic barrier: the overlapped quarantine must be visibly waiting on the held
        // Asset row lock before the lease is expired. A fixed sleep alone cannot prove this.
        await WaitForAssetRowLockWaiter();
        task.IsCompleted.Should().BeFalse();

        await using (ApplicationDbContext expiryDb = fixture.CreateDbContext())
        {
            await expiryDb.AssetProcessingJobs.Where(j => j.Id == claimed.JobId).ExecuteUpdateAsync(s =>
                s.SetProperty(j => j.LeaseExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)));
        }

        await holderTransaction.CommitAsync();
        AssetProcessingJobOutcome terminalResult = await task;
        terminalResult.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>();

        await using ApplicationDbContext verifierDb = fixture.CreateDbContext();
        (await verifierDb.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SafetyRestriction
            .Should().Be(AssetVersionSafetyRestriction.NONE);
        (await verifierDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId)).State
            .Should().Be(CodeAnalysisRunState.RUNNING);
        (await verifierDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId)).Status
            .Should().Be(AssetProcessingJobStatus.RUNNING);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenFailedEnvelopeIsProvided_ShouldLeaveRetryBoundaryCoherent()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            seedDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);

        // The real retryable failure envelope the service sends for this exact run. An envelope
        // missing any of its bindings is not this contract and is refused instead of applied.
        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion policy = await seedDb.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();
        var failureEnvelope = PythonAnalysisEnvelope.FailureEnvelope(
            PythonAnalysisEnvelope.Bind(run),
            new PythonAnalysisEnvelope.Options
            {
                Outcome = "FAILED",
                ErrorCode = "TOKENIZER_UNAVAILABLE",
                FailedStage = "TOKEN",
                Retryable = true
            },
            PythonAnalysisEnvelope.FailureShape.STAGE_FAILURE,
            policy);

        AssetProcessingJobOutcome outcome = await finalizer.FinalizeConfirmed(
            claimed.JobId,
            claimed.LeaseToken,
            failureEnvelope,
            CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.RETRY_SCHEDULED);

        await using ApplicationDbContext verifierDb = fixture.CreateDbContext();
        (await verifierDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId)).State
            .Should().Be(CodeAnalysisRunState.QUEUED);
        AssetProcessingJob retryJob = await verifierDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId);
        retryJob.Status.Should().Be(AssetProcessingJobStatus.RETRY_SCHEDULED);
        retryJob.AvailableAt.Should().BeAfter(DateTimeOffset.UtcNow);
        AnalysisCapacitySlot reservedSlot = await verifierDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        reservedSlot.State.Should().Be(AnalysisCapacitySlotState.RESERVED);

        // An unconfirmed outcome never releases capacity on its own: the reservation stays fenced.
        (await seed.Capacity.ReleaseReservation(
            reservedSlot.Id, claimed.TransportLeaseToken!, AnalysisExecutionDisposition.UNCONFIRMED))
            .Should().BeFalse();

        // The worker releases with the explicitly confirmed disposition, then the bounded retry
        // is actually claimed again once it is due.
        (await seed.Capacity.ReleaseForTerminalJob(
            claimed.JobId, claimed.TransportLeaseToken!, AnalysisExecutionDisposition.CONFIRMED_TERMINATED))
            .Should().BeTrue();
        AnalysisCapacitySlot freedSlot = await verifierDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        freedSlot.State.Should().Be(AnalysisCapacitySlotState.FREE);
        freedSlot.ReleasedReason.Should().Be(AnalysisReleaseReasons.CONFIRMED_TERMINATION);

        await using (ApplicationDbContext dueDb = fixture.CreateDbContext())
        {
            await dueDb.AssetProcessingJobs.Where(j => j.Id == claimed.JobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.AvailableAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        ClaimedAssetProcessingJob reclaimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();
        reclaimed.JobId.Should().Be(claimed.JobId);
        reclaimed.AttemptCount.Should().Be(claimed.AttemptCount + 1);
        CodeAnalysisRun reclaimedRun = await verifierDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        reclaimedRun.State.Should().Be(CodeAnalysisRunState.RUNNING);
        reclaimedRun.TransportLeaseToken.Should().Be(reclaimed.TransportLeaseToken);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenWorkspaceCleanupFailsAfterCompletedStages_ShouldPersistCleanupFailureWithoutAdmission()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "capacity-worker")).Single();

        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion policy = await seedDb.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();
        var cleanupEnvelope = PythonAnalysisEnvelope.FailureEnvelope(
            PythonAnalysisEnvelope.Bind(run),
            new PythonAnalysisEnvelope.Options { Outcome = "FAILED", ErrorCode = "CLEANUP_FAILED", FailedStage = "REPORT" },
            PythonAnalysisEnvelope.FailureShape.CLEANUP_FAILURE,
            policy);

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            seedDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        AssetProcessingJobOutcome outcome = await finalizer.FinalizeConfirmed(
            claimed.JobId, claimed.LeaseToken, cleanupEnvelope, CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.FAILED);
        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        CodeAnalysisRun failedRun = await verifyDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        AssetProcessingJob failedJob = await verifyDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId);
        failedRun.State.Should().Be(CodeAnalysisRunState.FAILED);
        failedRun.ErrorCode.Should().Be("CLEANUP_FAILED");
        failedJob.Status.Should().Be(AssetProcessingJobStatus.FAILED);
        failedJob.ErrorCode.Should().Be("CLEANUP_FAILED");
        (await verifyDb.CodeAnalysisReportHeaders.AsNoTracking().CountAsync(h => h.AnalysisRunId == seed.RunId)).Should().Be(0);
        (await verifyDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State.Should().Be(AnalysisCapacitySlotState.RESERVED);

        // Response confirms the execution ended; it does not claim plaintext cleanup succeeded.
        (await seed.Capacity.ReleaseForTerminalJob(
            claimed.JobId, claimed.TransportLeaseToken!, AnalysisExecutionDisposition.CONFIRMED_TERMINATED)).Should().BeTrue();
        (await verifyDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State.Should().Be(AnalysisCapacitySlotState.FREE);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenRetryAttemptsExhausted_ShouldFailRunAndJobCoherently()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();

        await using (ApplicationDbContext exhaustDb = fixture.CreateDbContext())
        {
            await exhaustDb.AssetProcessingJobs.Where(j => j.Id == claimed.JobId).ExecuteUpdateAsync(s =>
                s.SetProperty(j => j.AttemptCount, j => j.MaxAttempts));
        }

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            seedDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);

        // The same real failure envelope, delivered once the bounded attempts are exhausted.
        CodeAnalysisRun exhaustedRun = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion exhaustedPolicy = await seedDb.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();
        var exhaustedEnvelope = PythonAnalysisEnvelope.FailureEnvelope(
            PythonAnalysisEnvelope.Bind(exhaustedRun),
            new PythonAnalysisEnvelope.Options
            {
                Outcome = "FAILED",
                ErrorCode = "TOKENIZER_UNAVAILABLE",
                FailedStage = "TOKEN",
                Retryable = true
            },
            PythonAnalysisEnvelope.FailureShape.STAGE_FAILURE,
            exhaustedPolicy);
        AssetProcessingJobOutcome outcome = await finalizer.FinalizeConfirmed(
            claimed.JobId,
            claimed.LeaseToken,
            exhaustedEnvelope,
            CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.FAILED);

        await using ApplicationDbContext verifierDb = fixture.CreateDbContext();
        (await verifierDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId)).State
            .Should().Be(CodeAnalysisRunState.FAILED);
        (await verifierDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId)).Status
            .Should().Be(AssetProcessingJobStatus.FAILED);
        (await verifierDb.CodeAnalysisReportHeaders.AsNoTracking().CountAsync(h => h.AnalysisRunId == seed.RunId)).Should().Be(0);
    }

    [Fact]
    public async Task FinalizeFailure_WhenDeterministicPreStartRefusal_ShouldFailRunAndJobWithoutAdmissionOrQuarantine()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "capacity-worker")).Single();

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            seedDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        AssetProcessingJobOutcome outcome = await finalizer.FinalizeFailure(
            claimed.JobId, claimed.LeaseToken, "ANALYSIS_RESPONSE_INVALID", "The service refused invalid input.",
            ownerRetryable: false);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.FAILED);
        await using ApplicationDbContext verifierDb = fixture.CreateDbContext();
        CodeAnalysisRun failedRun = await verifierDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        AssetProcessingJob failedJob = await verifierDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId);
        failedRun.State.Should().Be(CodeAnalysisRunState.FAILED);
        failedRun.OwnerRetryable.Should().BeFalse();
        failedJob.Status.Should().Be(AssetProcessingJobStatus.FAILED);
        (await verifierDb.CodeAnalysisReportHeaders.AsNoTracking().CountAsync(h => h.AnalysisRunId == seed.RunId)).Should().Be(0);
        (await verifierDb.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SafetyRestriction
            .Should().Be(AssetVersionSafetyRestriction.NONE);
        (await verifierDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State.Should().Be(AnalysisCapacitySlotState.RESERVED);

        (await seed.Capacity.ReleaseForTerminalJob(
            claimed.JobId, claimed.TransportLeaseToken!, AnalysisExecutionDisposition.NOT_STARTED)).Should().BeTrue();
        (await verifierDb.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State.Should().Be(AnalysisCapacitySlotState.FREE);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenReplayRacesDelete_ShouldResolveReplayWithoutNewEffects()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();

        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.Chain.PolicyDigest);

        await using ApplicationDbContext firstDb = fixture.CreateDbContext();
        ICodeAnalysisReportFinalizer firstFinalizer = new CodeAnalysisReportFinalizer(
            firstDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        AssetProcessingJobOutcome first = await firstFinalizer.FinalizeConfirmed(
            claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        first.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();

        Guid? selectedBefore = (await seedDb.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SelectedCodeAnalysisReportId;
        var headersBefore = await seedDb.CodeAnalysisReportHeaders.AsNoTracking().CountAsync();
        var outboxesBefore = await seedDb.OutboxMessages.AsNoTracking().CountAsync();

        await using ApplicationDbContext deleterDb = fixture.CreateDbContext();
        await using ApplicationDbContext replayerDb = fixture.CreateDbContext();
        ICodeAnalysisReportFinalizer replayer = new CodeAnalysisReportFinalizer(
            replayerDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);

        Task<int> deleteTask = deleterDb.Assets.Where(a => a.Id == seed.Asset.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, DateTimeOffset.UtcNow));
        Task<AssetProcessingJobOutcome> replayTask = replayer.FinalizeConfirmed(
            claimed.JobId, Guid.NewGuid(), body, CancellationToken.None);
        await Task.WhenAll(deleteTask, replayTask);

        (await replayTask).Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        (await seedDb.CodeAnalysisReportHeaders.AsNoTracking().CountAsync()).Should().Be(headersBefore);
        (await seedDb.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(outboxesBefore);
        (await seedDb.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SelectedCodeAnalysisReportId
            .Should().Be(selectedBefore);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenFirstCompletionOverlapsSoftDelete_ShouldBeFencedByStoreLifecycle()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "capacity-worker")).Single();
        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.Chain.PolicyDigest);

        await using ApplicationDbContext deleteDb = fixture.CreateDbContext();
        await using IDbContextTransaction deleteTx = await deleteDb.Database.BeginTransactionAsync();
        IAssetStore assets = new AssetStore(deleteDb);
        (await assets.GetForUpdate(seed.Asset.Id)).Should().NotBeNull();

        await using ApplicationDbContext finalizerDb = fixture.CreateDbContext();
        await finalizerDb.Database.OpenConnectionAsync();
        var finalizerPid = await finalizerDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            finalizerDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        Task<AssetProcessingJobOutcome> finalizeTask = finalizer.FinalizeConfirmed(
            claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        await WaitForBackendLockWait(finalizerPid);

        await assets.SoftDelete(seed.Asset.Id, DateTimeOffset.UtcNow);
        await deleteTx.CommitAsync();
        (await finalizeTask.WaitAsync(TimeSpan.FromSeconds(20))).Should()
            .BeOfType<AssetProcessingJobOutcome.TerminalFailure>();

        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        (await verifyDb.CodeAnalysisReportHeaders.AsNoTracking().CountAsync(h => h.AnalysisRunId == seed.RunId)).Should().Be(0);
        (await verifyDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId)).State
            .Should().Be(CodeAnalysisRunState.CANCELLED);
        (await verifyDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId)).Status
            .Should().Be(AssetProcessingJobStatus.CANCELLED);
        (await verifyDb.Assets.AsNoTracking().SingleAsync(a => a.Id == seed.Asset.Id)).DeletedAt.Should().NotBeNull();
        (await verifyDb.SecurityEvidenceRecords.AsNoTracking().CountAsync(e =>
            e.Id == seed.Chain.ArchiveEvidenceId || e.Id == seed.Chain.MalwareEvidenceId)).Should().Be(2);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenDeclarationSaveWinsAssetLock_ShouldPersistOlderReportAsHistorical()
    {
        var saveBarrier = new SaveChangesBarrier();
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(seedDb, includeVersionWorkspace: true);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "capacity-worker")).Single();
        CodeAnalysisRun run = await seedDb.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.Chain.PolicyDigest);

        await using ApplicationDbContext saveDb = fixture.CreateDbContext(options => options.AddInterceptors(saveBarrier));
        IAssetStore saveAssetStore = new AssetStore(saveDb);
        IModerationFoundationStore moderation = new ModerationFoundationStore(
            saveDb, saveAssetStore, Substitute.For<IUserStore>(), new EfUnitOfWork(saveDb));
        Task<ModerationDraftSaveResult> saveTask = moderation.SaveDraftRevision(new DraftRevisionSaveRequest(
            seed.Asset.AuthorId, seed.Asset.Id, seed.Version.Id,
            ModerationOperationKinds.DECLARATION_SAVE, Guid.NewGuid(), new string('a', 64),
            "{\"schemaVersion\":1,\"items\":[]}", 1, new string('b', 64), ExpectedWorkspaceRevision: 1));
        await saveBarrier.BeforeSave.Task.WaitAsync(TimeSpan.FromSeconds(10));

        await using ApplicationDbContext finalizerDb = fixture.CreateDbContext();
        await finalizerDb.Database.OpenConnectionAsync();
        var finalizerPid = await finalizerDb.Database.SqlQueryRaw<int>("SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            finalizerDb, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        Task<AssetProcessingJobOutcome> finalizeTask = finalizer.FinalizeConfirmed(
            claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        await WaitForBackendLockWait(finalizerPid);

        saveBarrier.Release.TrySetResult();
        (await saveTask.WaitAsync(TimeSpan.FromSeconds(20))).Status.Should().Be(ModerationDraftSaveStatus.SUCCEEDED);
        (await finalizeTask.WaitAsync(TimeSpan.FromSeconds(20))).Should()
            .BeOfType<AssetProcessingJobOutcome.AtomicCommitted>().Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);

        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        CodeAnalysisReportHeader header = await verifyDb.CodeAnalysisReportHeaders.AsNoTracking()
            .SingleAsync(h => h.AnalysisRunId == seed.RunId);
        header.Disposition.Should().Be(CodeAnalysisReportDisposition.HISTORICAL_STALE_INPUT);
        (await verifyDb.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id))
            .SelectedCodeAnalysisReportId.Should().BeNull();
        AssetDraftWorkspace workspace = await verifyDb.AssetDraftWorkspaces.AsNoTracking()
            .SingleAsync(w => w.Id == seed.WorkspaceId);
        workspace.SourceDeclarationHeadRevision.Should().Be(1);
        (await verifyDb.AssetSourceDeclarationRevisions.AsNoTracking()
            .CountAsync(r => r.WorkspaceId == seed.WorkspaceId)).Should().Be(1);
    }

    /// <summary>
    /// Bounded deterministic barrier: waits until a second backend is observably blocked behind
    /// the held Asset row lock (an ungranted lock in pg_locks; a FOR UPDATE row wait surfaces as
    /// a transactionid wait on the holder). Fails explicitly when no waiter appears, so expiry
    /// can never be tested against a call that had not locked yet.
    /// </summary>
    private async Task WaitForAssetRowLockWaiter()
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using ApplicationDbContext db = fixture.CreateDbContext();
            var waiting = await db.Database.SqlQueryRaw<int>(
                """SELECT COUNT(*) AS "Value" FROM pg_locks WHERE NOT granted""")
                .SingleAsync();
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException("The overlapped finalizer never reached its Asset lock wait.");
    }

    private async Task WaitForBackendLockWait(int pid)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using ApplicationDbContext observer = fixture.CreateDbContext();
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

    private sealed class LateAuditFaultInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (command.CommandText.Contains("INSERT INTO audit_logs", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("CODE_ANALYSIS_FINALIZED", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Injected finalizer fault.");
            }

            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(NonQueryExecuting(command, eventData, result));
    }

    private static string BuildResponseBody(CodeAnalysisRun run, AssetVersion version, Asset asset, AdmittedReportTestEvidence.Chain chain, string policyDigest) =>
        CodeAnalysisReportFinalizerPostgresTests.BuildResponseBody(run, version, asset, chain, policyDigest);

    private sealed record SeedContext(Asset Asset, AssetVersion Version, Guid RunId, AdmittedReportTestEvidence.Chain Chain, IAnalysisCapacityStore Capacity, string PolicyDigest, Guid WorkspaceId);

    private static async Task<SeedContext> SeedQueued(ApplicationDbContext db, bool includeVersionWorkspace = false)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Finalizer concurrency subject");
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        Guid? workspaceId = null;
        var declarationDigest = ProductDigests.EmptyProjectionDigest(nameof(AssetSourceDeclarationRevision));
        var materialDigest = ProductDigests.EmptyProjectionDigest(nameof(AssetMaterialMetadataRevision));
        if (includeVersionWorkspace)
        {
            IAssetStore assetStore = new AssetStore(db);
            IModerationFoundationStore moderation = new ModerationFoundationStore(
                db, assetStore, Substitute.For<IUserStore>(), new EfUnitOfWork(db));
            AssetDraftWorkspaceSnapshot preUpload = await moderation.EnsurePreUploadWorkspace(asset.Id, author.Id);
            VersionAttachResult attached = await moderation.AttachUploadedVersionToWorkspace(
                asset.Id, version.Id, author.Id, preUpload.WorkspaceId, preUpload.WorkspaceRevision);
            attached.Status.Should().Be(VersionAttachStatus.ATTACHED);
            workspaceId = attached.VersionWorkspace!.WorkspaceId;
        }

        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain() with { PolicyDigest = ReviewAnalysisPolicy.DIGEST };
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        await AdmittedReportTestEvidence.SeedObservedProofAsync(db, version, chain);
        await db.SaveChangesAsync();

        Guid runId = chain.RunId;
        var jobId = Guid.NewGuid();

        CodeAnalysisRun run = new()
        {
            Id = runId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            PolicyVersionId = chain.PolicyVersionId,
            PolicyDigest = chain.PolicyDigest,
            DeclarationDigest = declarationDigest,
            MaterialDigest = materialDigest,
            InputDigest = chain.InputDigest,
            SelectionGeneration = 1,
            AssetDraftWorkspaceId = workspaceId,
            JobId = null,
            ArchiveEvidenceId = chain.ArchiveEvidenceId,
            MalwareEvidenceId = chain.MalwareEvidenceId,
            SecurityGeneration = 1,
            ServiceKey = SERVICE_KEY,
            ReservationGeneration = null,
            State = CodeAnalysisRunState.QUEUED,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.CodeAnalysisRuns.Add(run);
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
                    runId, chain.InputDigest, chain.PolicyDigest, version.ContentSha256,
                    chain.ArchiveEvidenceId, chain.MalwareEvidenceId, 1))
        });
        await db.SaveChangesAsync();
        await db.CodeAnalysisRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, jobId));
        db.ChangeTracker.Clear();

        IOptions<CodeAnalysisOptions> codeOptions = MsOptions.Create(new CodeAnalysisOptions
        {
            Enabled = true,
            ServiceKey = SERVICE_KEY,
            PolicyKey = POLICY_KEY,
            PolicyDigest = chain.PolicyDigest,
            Capacity = 1
        });

        IAnalysisCapacityStore capacity = new AnalysisCapacityStore(db, codeOptions, NullLogger<AnalysisCapacityStore>.Instance);
        return new SeedContext(asset, version, runId, chain, capacity, chain.PolicyDigest, workspaceId ?? Guid.Empty);
    }

    private sealed class SaveChangesBarrier : SaveChangesInterceptor
    {
        public TaskCompletionSource BeforeSave { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            BeforeSave.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}






