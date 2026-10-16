using System.Data;
using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// Capacity-aware claim for the shared analysis service. Slot rows are the only scheduler for
/// analysis work: the general queue never claims an analysis job, so a busy service can never
/// consume attempts through ordinary retries.
///
/// Every reservation mutation is one short transaction in slot -> run -> job order, reads the database clock only
/// after the locks it depends on are held, and asserts each affected row count before success.
/// </summary>
internal sealed class AnalysisCapacityStore(
    ApplicationDbContext dbContext,
    IOptions<CodeAnalysisOptions> analysisOptions,
    ILogger<AnalysisCapacityStore> logger) : IAnalysisCapacityStore
{
    private sealed class RegistrationRow
    {
        public int Capacity { get; init; }
    }

    private sealed class SlotRow
    {
        public Guid Id { get; init; }
        public int SlotNumber { get; init; }
        public string State { get; init; } = "";
        public string? LeaseToken { get; init; }
        public int? ReservationGeneration { get; init; }
        public Guid? ReservedJobId { get; init; }
        public Guid? ReservedRunId { get; init; }
    }

    private sealed class JobLeaseRow
    {
        public Guid Id { get; init; }
        public string Status { get; init; } = "";
        public Guid? LeaseToken { get; init; }
        public DateTimeOffset? LeaseExpiresAt { get; init; }
        public string? TransportLeaseToken { get; init; }
        public string? ErrorCode { get; init; }
    }

    private sealed class RunLeaseRow
    {
        public Guid Id { get; init; }
        public string State { get; init; } = "";
        public string? TransportLeaseToken { get; init; }
        public int? ReservationGeneration { get; init; }
    }

    private sealed class CandidateRow
    {
        public Guid JobId { get; init; }
        public Guid RunId { get; init; }
    }

    public async Task<int> EnsureSlots(string serviceKey, int capacity, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, CodeAnalysisOptions.MIN_CAPACITY);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(capacity, CodeAnalysisOptions.MAX_CAPACITY);

        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // The registration row is the serialization point: the loser of a concurrent insert waits on
        // this row lock and then sees the winner's capacity.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO analysis_service_registrations ("Id", "ServiceKey", "Capacity", "CreatedAt", "RegisteredAt")
            VALUES ({Guid.NewGuid()}, {serviceKey}, {capacity}, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow})
            ON CONFLICT ("ServiceKey") DO NOTHING
            """, cancellationToken);

        List<RegistrationRow> registrations = await dbContext.Database.SqlQueryRaw<RegistrationRow>(
            """
            SELECT "Capacity"
            FROM analysis_service_registrations
            WHERE "ServiceKey" = {0}
            FOR UPDATE
            """, serviceKey).ToListAsync(cancellationToken);

        if (registrations.Count != 1)
        {
            throw new InvalidOperationException($"Analysis service '{serviceKey}' could not be registered.");
        }

        // A configuration mismatch fails closed instead of over-claiming a differently sized service.
        if (registrations[0].Capacity != capacity)
        {
            throw new InvalidOperationException(
                $"Configured analysis capacity {capacity} does not match the registered capacity "
                + $"{registrations[0].Capacity} for service '{serviceKey}'.");
        }

        for (var slotNumber = 0; slotNumber < capacity; slotNumber++)
        {
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO analysis_capacity_slots ("Id", "ServiceKey", "SlotNumber", "Capacity", "State", "ReleasedReason", "CreatedAt")
                VALUES ({Guid.NewGuid()}, {serviceKey}, {slotNumber}, {capacity}, 'FREE', 'REGISTERED', {DateTimeOffset.UtcNow})
                ON CONFLICT ("ServiceKey", "SlotNumber") DO NOTHING
                """, cancellationToken);
        }

        List<Guid> persisted = await dbContext.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" AS "Value" FROM analysis_capacity_slots WHERE "ServiceKey" = {0} FOR UPDATE""", serviceKey)
            .ToListAsync(cancellationToken);

        if (persisted.Count != capacity)
        {
            throw new InvalidOperationException($"Analysis service '{serviceKey}' has fewer slots than its registered capacity.");
        }

        await tx.CommitAsync(cancellationToken);
        return capacity;
    }

    public async Task<IReadOnlyList<ClaimedAssetProcessingJob>> ClaimWithCapacity(
        int batchSize,
        TimeSpan leaseDuration,
        TimeSpan hardDeadline,
        TimeSpan cleanupGrace,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        if (!analysisOptions.Value.Enabled)
        {
            return [];
        }

        var serviceKey = analysisOptions.Value.ServiceKey;
        var claimed = new List<ClaimedAssetProcessingJob>(batchSize);

        for (var i = 0; i < batchSize; i++)
        {
            ClaimedAssetProcessingJob? job = await ClaimOne(
                serviceKey, leaseDuration, hardDeadline, cleanupGrace, leaseOwner, cancellationToken);

            if (job is null)
            {
                break;
            }

            claimed.Add(job);
        }

        return claimed;
    }

    /// <summary>
    /// One short slot -> run -> job transaction. Every write asserts its row count, so a lost race
    /// rolls the whole reservation back instead of leaving a half-bound run or job.
    /// </summary>
    private async Task<ClaimedAssetProcessingJob?> ClaimOne(
        string serviceKey,
        TimeSpan leaseDuration,
        TimeSpan hardDeadline,
        TimeSpan cleanupGrace,
        string leaseOwner,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        List<SlotRow> freeSlots = await dbContext.Database.SqlQueryRaw<SlotRow>(
            """
            SELECT "Id", "SlotNumber", "State"::text AS "State", "LeaseToken", "ReservationGeneration",
                   "ReservedJobId", "ReservedRunId"
            FROM analysis_capacity_slots
            WHERE "ServiceKey" = {0} AND "State" = 'FREE'
            ORDER BY "SlotNumber"
            FOR UPDATE SKIP LOCKED
            LIMIT 1
            """, serviceKey).ToListAsync(cancellationToken);

        if (freeSlots.Count == 0)
        {
            // No capacity means no claim at all: the job stays queued and its attempts are untouched.
            await tx.CommitAsync(cancellationToken);
            return null;
        }

        SlotRow slot = freeSlots[0];

        // The candidate job must belong to a run that targets this exact service.
        List<CandidateRow> candidates = await dbContext.Database.SqlQueryRaw<CandidateRow>(
            """
            SELECT j."Id" AS "JobId", r."Id" AS "RunId"
            FROM asset_processing_jobs AS j
            JOIN code_analysis_runs AS r ON r."Id" = j."AnalysisRunId"
            WHERE j."Type" = 'CODE_ANALYSIS'
              AND j."Status" IN ('QUEUED', 'RETRY_SCHEDULED')
              AND j."AvailableAt" <= clock_timestamp()
              AND j."AttemptCount" < j."MaxAttempts"
              AND r."ServiceKey" = {0}
              AND r."State" = 'QUEUED'
            ORDER BY j."AvailableAt", j."Id"
            FOR UPDATE OF r SKIP LOCKED
            LIMIT 1
            """, serviceKey).ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return null;
        }

        CandidateRow candidate = candidates[0];
        List<JobLeaseRow> candidateJobs = await LockJobForUpdate(candidate.JobId, cancellationToken);
        if (candidateJobs.Count != 1
            || (candidateJobs[0].Status != "QUEUED" && candidateJobs[0].Status != "RETRY_SCHEDULED"))
        {
            await tx.CommitAsync(cancellationToken);
            return null;
        }

        var leaseToken = Guid.NewGuid();
        var transportLeaseToken = CreateTransportLeaseToken();

        List<int> generationRows = await dbContext.Database.SqlQueryRaw<int>(
            """
            SELECT COALESCE(MAX("ReservationGeneration"), 0) AS "Value"
            FROM analysis_capacity_slots
            WHERE "ServiceKey" = {0} AND "SlotNumber" = {1}
            """, serviceKey, slot.SlotNumber).ToListAsync(cancellationToken);

        var reservationGeneration = (generationRows.Count == 0 ? 0 : generationRows[0]) + 1;
        DateTimeOffset hardDeadlineUtc = await ReadClock(cancellationToken).ConfigureAwait(false) + hardDeadline;

        var reserved = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE analysis_capacity_slots
            SET "State" = 'RESERVED',
                "ReservedRunId" = {candidate.RunId},
                "ReservedJobId" = {candidate.JobId},
                "LeaseToken" = {transportLeaseToken},
                "ReservationGeneration" = {reservationGeneration},
                "HardDeadlineUtc" = {hardDeadlineUtc},
                "CleanupGraceUntil" = {hardDeadlineUtc} + {cleanupGrace},
                "ConfirmedTerminationAt" = NULL,
                "ReleasedReason" = NULL,
                "ReleasedAt" = NULL,
                "ReservedAt" = clock_timestamp(),
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {slot.Id} AND "State" = 'FREE'
            """, cancellationToken);

        if (reserved != 1)
        {
            await tx.RollbackAsync(cancellationToken);
            return null;
        }

        var claimedCount = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs AS j
            SET "Status" = 'RUNNING',
                "Stage" = 'RUNNING',
                "LeaseOwner" = {leaseOwner},
                "LeaseToken" = {leaseToken},
                "LeaseExpiresAt" = clock_timestamp() + {leaseDuration},
                "StartedAt" = COALESCE(j."StartedAt", clock_timestamp()),
                "AttemptCount" = j."AttemptCount" + 1,
                "TransportLeaseToken" = {transportLeaseToken},
                "UpdatedAt" = clock_timestamp()
            WHERE j."Id" = {candidate.JobId}
              AND j."Status" IN ('QUEUED', 'RETRY_SCHEDULED')
              AND j."AttemptCount" < j."MaxAttempts"
            """, cancellationToken);

        if (claimedCount != 1)
        {
            await tx.RollbackAsync(cancellationToken);
            return null;
        }

        var bound = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE code_analysis_runs
            SET "TransportLeaseToken" = {transportLeaseToken},
                "ReservationGeneration" = {reservationGeneration},
                "HardDeadlineUtc" = {hardDeadlineUtc},
                "State" = 'RUNNING',
                "StartedAt" = COALESCE("StartedAt", clock_timestamp()),
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {candidate.RunId}
              AND "State" = 'QUEUED'
              AND "ServiceKey" = {serviceKey}
            """, cancellationToken);

        if (bound != 1)
        {
            // The run, job and slot must move together; a partial claim is never left behind.
            await tx.RollbackAsync(cancellationToken);
            return null;
        }

        List<ClaimedAssetProcessingJob> claimedJobs = await dbContext.AssetProcessingJobs
            .AsNoTracking()
            .Where(j => j.Id == candidate.JobId)
            .Select(j => new ClaimedAssetProcessingJob(
                j.Id,
                j.AssetId,
                j.AssetVersionId,
                j.Asset.AuthorId,
                j.Type,
                j.DefinitionVersion,
                j.AttemptCount,
                j.MaxAttempts,
                j.Payload!,
                j.TraceParent,
                j.LeaseToken!.Value,
                j.AvailableAt,
                j.CreatedAt,
                j.UpdatedAt,
                j.AnalysisRunId,
                j.SecurityRecheckId,
                j.TransportLeaseToken))
            .ToListAsync(cancellationToken);

        if (claimedJobs.Count != 1)
        {
            await tx.RollbackAsync(cancellationToken);
            return null;
        }

        await tx.CommitAsync(cancellationToken);
        return claimedJobs[0];
    }

    public async Task<bool> RenewAnalysisLease(
        Guid jobId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Slot -> run -> job -> clock. Re-read both live bindings after acquiring their locks.
        List<SlotRow> slots = await dbContext.Database.SqlQueryRaw<SlotRow>(
            """
            SELECT "Id", "SlotNumber", "State"::text AS "State", "LeaseToken", "ReservationGeneration",
                   "ReservedJobId", "ReservedRunId"
            FROM analysis_capacity_slots
            WHERE "ReservedJobId" = {0} AND "State" = 'RESERVED'
            FOR UPDATE
            """, jobId).ToListAsync(cancellationToken);

        if (slots.Count != 1)
        {
            return false;
        }

        SlotRow slot = slots[0];
        if (slot.ReservedRunId is not { } runId)
        {
            return false;
        }

        List<RunLeaseRow> runs = await LockRunForUpdate(runId, cancellationToken);
        if (runs.Count != 1
            || runs[0].State != "RUNNING"
            || runs[0].TransportLeaseToken != slot.LeaseToken
            || runs[0].ReservationGeneration != slot.ReservationGeneration)
        {
            return false;
        }

        List<JobLeaseRow> jobs = await LockJobForUpdate(jobId, cancellationToken);
        if (jobs.Count != 1)
        {
            return false;
        }

        DateTimeOffset dbNow = await ReadClock(cancellationToken);
        JobLeaseRow job = jobs[0];

        if (job.Status != "RUNNING" || job.LeaseToken != leaseToken || job.LeaseExpiresAt <= dbNow)
        {
            return false;
        }

        // Only the job lease moves. The reservation's hard deadline is immutable.
        var renewed = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "LeaseExpiresAt" = {dbNow} + {leaseDuration},
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {jobId}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {leaseToken}
              AND "LeaseExpiresAt" > {dbNow}
            """, cancellationToken);

        if (renewed != 1)
        {
            return false;
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> ReleaseForTerminalJob(
        Guid jobId,
        string transportLeaseToken,
        AnalysisExecutionDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        List<SlotRow> slots = await dbContext.Database.SqlQueryRaw<SlotRow>(
            """
            SELECT "Id", "SlotNumber", "State"::text AS "State", "LeaseToken", "ReservationGeneration",
                   "ReservedJobId", "ReservedRunId"
            FROM analysis_capacity_slots
            WHERE "ReservedJobId" = {0} AND "State" <> 'FREE'
            """, jobId).ToListAsync(cancellationToken);

        if (slots.Count != 1 || slots[0].LeaseToken != transportLeaseToken)
        {
            return false;
        }

        return await ReleaseReservation(slots[0].Id, transportLeaseToken, disposition, cancellationToken);
    }

    public async Task<bool> ReleaseReservation(
        Guid slotId,
        string transportLeaseToken,
        AnalysisExecutionDisposition disposition,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        List<SlotRow> locked = await dbContext.Database.SqlQueryRaw<SlotRow>(
            """
            SELECT "Id", "SlotNumber", "State"::text AS "State", "LeaseToken", "ReservationGeneration",
                   "ReservedJobId", "ReservedRunId"
            FROM analysis_capacity_slots
            WHERE "Id" = {0}
            FOR UPDATE
            """, slotId).ToListAsync(cancellationToken);

        if (locked.Count != 1 || locked[0].LeaseToken != transportLeaseToken || locked[0].State == "FREE")
        {
            return false;
        }

        SlotRow slot = locked[0];
        if (slot.ReservedJobId is not { } jobId)
        {
            return false;
        }

        if (slot.ReservedRunId is not { } runId)
        {
            return false;
        }

        List<RunLeaseRow> runs = await LockRunForUpdate(runId, cancellationToken);
        if (runs.Count != 1)
        {
            return false;
        }

        RunLeaseRow run = runs[0];
        var reservationStillBound = run.TransportLeaseToken == transportLeaseToken
            && run.ReservationGeneration == slot.ReservationGeneration;
        // A retryable, validated service failure clears the run's live lease before the worker has
        // confirmed process termination. The slot remains the authoritative reservation until that
        // independent confirmation (or enforced deadline plus grace) arrives.
        var retryAwaitingTermination = run is { State: "QUEUED", TransportLeaseToken: null, ReservationGeneration: null };
        if (!reservationStillBound && !retryAwaitingTermination)
        {
            return false;
        }

        List<JobLeaseRow> jobs = await LockJobForUpdate(jobId, cancellationToken);
        if (jobs.Count != 1)
        {
            return false;
        }

        DateTimeOffset dbNow = await ReadClock(cancellationToken);
        JobLeaseRow job = jobs[0];

        DateTimeOffset? graceUntil = await dbContext.Database
            .SqlQueryRaw<DateTimeOffset?>(
                """SELECT "CleanupGraceUntil" AS "Value" FROM analysis_capacity_slots WHERE "Id" = {0}""", slotId)
            .FirstAsync(cancellationToken);

        var jobCompleted = job.Status == "SUCCEEDED";
        var deadlineEnforced = graceUntil is { } grace && grace <= dbNow;

        // Only an independent fact frees capacity: an execution actually confirmed to have ended, a
        // refusal proven to precede acceptance, or the enforced deadline plus grace. A terminal job
        // is a fact about the job and never proof that the external execution stopped, so it cannot
        // release an unconfirmed reservation on its own.
        DateTimeOffset? confirmedAt = null;
        string? reason = null;

        if (disposition == AnalysisExecutionDisposition.CONFIRMED_TERMINATED)
        {
            confirmedAt = dbNow;
            reason = jobCompleted ? AnalysisReleaseReasons.COMPLETED : AnalysisReleaseReasons.CONFIRMED_TERMINATION;
        }
        else if (disposition == AnalysisExecutionDisposition.NOT_STARTED)
        {
            // The service refused before accepting any work, so there is no execution to confirm and
            // no termination timestamp to record.
            reason = AnalysisReleaseReasons.NOT_STARTED_REFUND;
        }
        else if (deadlineEnforced)
        {
            // The enforced deadline plus grace is the only fence available for an unconfirmed execution.
            reason = job.ErrorCode == ErrorCodes.LEASE_EXPIRED
                ? AnalysisReleaseReasons.LEASE_RECOVERED
                : AnalysisReleaseReasons.DEADLINE_RECONCILED;
        }

        if (reason is null)
        {
            logger.LogWarning(
                "Slot {SlotId} was not released: the reserved execution may still be running and no independent confirmation exists.",
                slotId);
            return false;
        }

        var released = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE analysis_capacity_slots
            SET "State" = 'FREE',
                "ReservedRunId" = NULL,
                "ReservedJobId" = NULL,
                "LeaseToken" = NULL,
                "ReservationGeneration" = NULL,
                "HardDeadlineUtc" = NULL,
                "CleanupGraceUntil" = NULL,
                "ConfirmedTerminationAt" = {confirmedAt},
                "ReleasedReason" = {reason},
                "ReleasedAt" = {dbNow},
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {slotId}
              AND "LeaseToken" = {transportLeaseToken}
              AND "State" <> 'FREE'
            """, cancellationToken);

        if (released != 1)
        {
            return false;
        }

        if (reservationStillBound)
        {
            var cleared = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE code_analysis_runs
                SET "TransportLeaseToken" = NULL,
                    "ReservationGeneration" = NULL,
                    "HardDeadlineUtc" = NULL,
                    "UpdatedAt" = {dbNow}
                WHERE "Id" = {runId}
                  AND "TransportLeaseToken" = {transportLeaseToken}
                  AND "ReservationGeneration" = {slot.ReservationGeneration}
                """, cancellationToken);

            if (cleared != 1)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RefundNotStartedClaim(
        Guid jobId,
        Guid leaseToken,
        string transportLeaseToken,
        int reservationGeneration,
        CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Slot -> run -> job, matching finalization and every capacity mutation.
        List<SlotRow> slots = await dbContext.Database.SqlQueryRaw<SlotRow>(
            """
            SELECT "Id", "SlotNumber", "State"::text AS "State", "LeaseToken", "ReservationGeneration",
                   "ReservedJobId", "ReservedRunId"
            FROM analysis_capacity_slots
            WHERE "ReservedJobId" = {0} AND "State" = 'RESERVED'
            FOR UPDATE
            """, jobId).ToListAsync(cancellationToken);

        if (slots.Count != 1)
        {
            return false;
        }

        SlotRow slot = slots[0];

        // The refund must carry this claim's own reservation identity.
        if (slot.LeaseToken != transportLeaseToken || slot.ReservationGeneration != reservationGeneration)
        {
            return false;
        }

        if (slot.ReservedRunId is not { } runId)
        {
            return false;
        }

        List<RunLeaseRow> runs = await LockRunForUpdate(runId, cancellationToken);
        if (runs.Count != 1
            || runs[0].State != "RUNNING"
            || runs[0].TransportLeaseToken != transportLeaseToken
            || runs[0].ReservationGeneration != reservationGeneration)
        {
            return false;
        }

        List<JobLeaseRow> jobs = await LockJobForUpdate(jobId, cancellationToken);
        if (jobs.Count != 1)
        {
            return false;
        }

        // The clock is read after the job lock, so an expiry during the wait is judged correctly.
        DateTimeOffset dbNow = await ReadClock(cancellationToken);
        JobLeaseRow job = jobs[0];

        if (job.Status != "RUNNING"
            || job.LeaseToken != leaseToken
            || job.LeaseExpiresAt <= dbNow
            || job.TransportLeaseToken != transportLeaseToken)
        {
            return false;
        }

        var refunded = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "AttemptCount" = GREATEST("AttemptCount" - 1, 0),
                "Status" = 'QUEUED',
                "Stage" = 'BUSY_NOT_STARTED',
                "ErrorCode" = {ErrorCodes.ANALYSIS_SERVICE_BUSY},
                "ErrorSummary" = 'The analysis service refused the request before starting any work.',
                "AvailableAt" = {dbNow},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "StartedAt" = NULL,
                "CompletedAt" = NULL,
                "TransportLeaseToken" = NULL,
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {jobId}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {leaseToken}
              AND "LeaseExpiresAt" > {dbNow}
              AND "TransportLeaseToken" = {transportLeaseToken}
            """, cancellationToken);

        if (refunded != 1)
        {
            return false;
        }

        {
            var runReset = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE code_analysis_runs
                SET "State" = 'QUEUED',
                    "TransportLeaseToken" = NULL,
                    "ReservationGeneration" = NULL,
                    "HardDeadlineUtc" = NULL,
                    "ErrorCode" = {ErrorCodes.ANALYSIS_SERVICE_BUSY},
                    "UpdatedAt" = {dbNow}
                WHERE "Id" = {runId}
                  AND "State" = 'RUNNING'
                  AND "TransportLeaseToken" = {transportLeaseToken}
                  AND "ReservationGeneration" = {reservationGeneration}
                """, cancellationToken);

            if (runReset != 1)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }
        }

        var freed = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE analysis_capacity_slots
            SET "State" = 'FREE',
                "ReservedRunId" = NULL,
                "ReservedJobId" = NULL,
                "LeaseToken" = NULL,
                "ReservationGeneration" = NULL,
                "HardDeadlineUtc" = NULL,
                "CleanupGraceUntil" = NULL,
                "ConfirmedTerminationAt" = {dbNow},
                "ReleasedReason" = {AnalysisReleaseReasons.NOT_STARTED_REFUND},
                "ReleasedAt" = {dbNow},
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {slot.Id}
              AND "LeaseToken" = {transportLeaseToken}
              AND "ReservationGeneration" = {reservationGeneration}
              AND "State" = 'RESERVED'
            """, cancellationToken);

        if (freed != 1)
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> RecoverExpiredReservations(CancellationToken cancellationToken = default)
    {
        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        List<SlotRow> expired = await dbContext.Database.SqlQueryRaw<SlotRow>(
            """
            SELECT "Id", "SlotNumber", "State"::text AS "State", "LeaseToken", "ReservationGeneration",
                   "ReservedJobId", "ReservedRunId"
            FROM analysis_capacity_slots
            WHERE "State" IN ('RESERVED', 'RELEASING', 'RECOVERING')
              AND "CleanupGraceUntil" IS NOT NULL
              AND "CleanupGraceUntil" <= clock_timestamp()
            ORDER BY "CleanupGraceUntil", "Id"
            FOR UPDATE SKIP LOCKED
            LIMIT 20
            """).ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return 0;
        }

        DateTimeOffset dbNow = await ReadClock(cancellationToken);
        var recovered = 0;

        foreach (SlotRow slot in expired)
        {
            // Only the exact reservation this row carries is touched; another service's or another
            // attempt's bindings are never cleared by a global predicate.
            var exhausted = false;
            List<RunLeaseRow> lockedRuns = slot.ReservedRunId is { } runIdToLock
                ? await LockRunForUpdate(runIdToLock, cancellationToken)
                : [];

            if (slot.ReservedJobId is { } jobId)
            {
                List<JobLeaseRow> jobs = await LockJobForUpdate(jobId, cancellationToken);
                if (jobs is [{ Status: "RUNNING" }] && jobs[0].TransportLeaseToken == slot.LeaseToken)
                {
                    List<AttemptRow> attempts = await dbContext.Database.SqlQueryRaw<AttemptRow>(
                        """SELECT "AttemptCount", "MaxAttempts" FROM asset_processing_jobs WHERE "Id" = {0}""", jobId)
                        .ToListAsync(cancellationToken);

                    if (attempts.Count == 1)
                    {
                        var resultingAttemptCount = attempts[0].AttemptCount;

                        // A recoverable reservation consumes this attempt, so a genuine execution
                        // failure still gets its bounded retries.
                        if (resultingAttemptCount < attempts[0].MaxAttempts)
                        {
                            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                                UPDATE asset_processing_jobs
                                SET "AttemptCount" = "AttemptCount" + 1
                                WHERE "Id" = {jobId} AND "Status" = 'RUNNING'
                                """, cancellationToken);
                            resultingAttemptCount++;
                        }

                        // Both the run and job transition use the post-accounting result. In
                        // particular, a recovery that consumes the last attempt must fail both.
                        exhausted = resultingAttemptCount >= attempts[0].MaxAttempts;
                    }
                }

                await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE asset_processing_jobs
                    SET "Status" = CASE WHEN {exhausted} THEN 'FAILED' ELSE 'RETRY_SCHEDULED' END,
                        "Stage" = CASE WHEN {exhausted} THEN 'FAILED_RESERVATION_RECONCILED' ELSE 'RESERVATION_RECONCILED' END,
                        "CompletedAt" = CASE WHEN {exhausted} THEN {dbNow} ELSE NULL END,
                        "AvailableAt" = CASE WHEN {exhausted} THEN "AvailableAt" ELSE {dbNow} END,
                        "ErrorCode" = {ErrorCodes.LEASE_EXPIRED},
                        "ErrorSummary" = 'The reserved analysis execution could no longer be confirmed as running.',
                        "LeaseOwner" = NULL,
                        "LeaseToken" = NULL,
                        "LeaseExpiresAt" = NULL,
                        "TransportLeaseToken" = NULL,
                        "UpdatedAt" = {dbNow}
                    WHERE "Id" = {jobId}
                      AND "Status" = 'RUNNING'
                      AND "TransportLeaseToken" = {slot.LeaseToken}
                    """, cancellationToken);
            }

            if (slot.ReservedRunId is { } runId)
            {
                if (lockedRuns is [{ State: "RUNNING" }]
                    && lockedRuns[0].TransportLeaseToken == slot.LeaseToken
                    && lockedRuns[0].ReservationGeneration == slot.ReservationGeneration)
                {
                    // Keep the run coherent with the job transition under the run-before-job locks.
                    await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                        UPDATE code_analysis_runs
                        SET "State" = CASE WHEN {exhausted} THEN 'FAILED' ELSE 'QUEUED' END,
                            "CompletedAt" = CASE WHEN {exhausted} THEN {dbNow} ELSE NULL END,
                            "OwnerRetryable" = CASE WHEN {exhausted} THEN true ELSE "OwnerRetryable" END,
                            "TransportLeaseToken" = NULL,
                            "ReservationGeneration" = NULL,
                            "HardDeadlineUtc" = NULL,
                            "ErrorCode" = {ErrorCodes.LEASE_EXPIRED},
                            "ErrorSummary" = 'The reserved analysis execution could no longer be confirmed as running.',
                            "UpdatedAt" = {dbNow}
                        WHERE "Id" = {runId}
                          AND "State" = 'RUNNING'
                          AND "TransportLeaseToken" = {slot.LeaseToken}
                          AND "ReservationGeneration" = {slot.ReservationGeneration}
                        """, cancellationToken);
                }
            }

            var freed = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE analysis_capacity_slots
                SET "State" = 'FREE',
                    "ReservedRunId" = NULL,
                    "ReservedJobId" = NULL,
                    "LeaseToken" = NULL,
                    "ReservationGeneration" = NULL,
                    "HardDeadlineUtc" = NULL,
                    "CleanupGraceUntil" = NULL,
                    "ConfirmedTerminationAt" = NULL,
                    "ReleasedReason" = {AnalysisReleaseReasons.DEADLINE_RECONCILED},
                    "ReleasedAt" = {dbNow},
                    "UpdatedAt" = {dbNow}
                WHERE "Id" = {slot.Id}
                  AND "State" <> 'FREE'
                  AND "LeaseToken" = {slot.LeaseToken}
                """, cancellationToken);

            if (freed == 1)
            {
                recovered++;
            }
        }

        await tx.CommitAsync(cancellationToken);

        if (recovered > 0)
        {
            logger.LogWarning("Reconciled {Count} analysis capacity reservations past their enforced deadline.", recovered);
        }

        return recovered;
    }

    private Task<List<JobLeaseRow>> LockJobForUpdate(Guid jobId, CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<JobLeaseRow>(
            """
            SELECT "Id", "Status", "LeaseToken", "LeaseExpiresAt", "TransportLeaseToken", "ErrorCode"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
            FOR UPDATE
            """, jobId).ToListAsync(cancellationToken);

    private Task<List<RunLeaseRow>> LockRunForUpdate(Guid runId, CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<RunLeaseRow>(
            """
            SELECT "Id", "State"::text AS "State", "TransportLeaseToken", "ReservationGeneration"
            FROM code_analysis_runs
            WHERE "Id" = {0}
            FOR UPDATE
            """, runId).ToListAsync(cancellationToken);

    private Task<DateTimeOffset> ReadClock(CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

    /// <summary>
    /// Builds the explicit persisted transport binding the service requires: 64 lowercase hex
    /// characters. The job lease stays a Guid and is never formatted as a service token.
    /// </summary>
    private static string CreateTransportLeaseToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    private sealed class AttemptRow
    {
        public int AttemptCount { get; init; }
        public int MaxAttempts { get; init; }
    }
}
