using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
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
        OutboxMessage intent = await PersistIntent(db, seed);

        await CreateHandler(db, seed.PolicyDigest).Handle(intent, CancellationToken.None);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync();
        AssetProcessingJob job = await db.AssetProcessingJobs.AsNoTracking()
            .SingleAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS);

        run.ArchiveEvidenceId.Should().Be(seed.ArchiveEvidenceId);
        run.MalwareEvidenceId.Should().Be(seed.MalwareEvidenceId);
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

    private static async Task<OutboxMessage> PersistIntent(ApplicationDbContext db, SeededProof seed)
    {
        await CreateEnqueuer(db, seed.PolicyDigest).PersistSchedulingIntent(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        return await db.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Type == OutboxMessageTypes.ANALYSIS_SCHEDULING);
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
