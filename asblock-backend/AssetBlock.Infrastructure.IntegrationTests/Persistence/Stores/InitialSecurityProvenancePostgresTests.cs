using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing.Handlers;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Publication;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Proves initial-upload security evidence is observed, not fabricated, and that the CLEAN
/// promotion, its proof and the durable analysis scheduling commit atomically. READY-version
/// rechecks route through their dedicated scoped finalizers and never the initial lifecycle.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class InitialSecurityProvenancePostgresTests(PostgresFixture fixture)
{
    private const string POLICY_KEY = "code-analysis-policy-v1";
    private const string SERVICE_KEY = "code-analysis";

    private static readonly byte[] _content = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
    private static readonly string _contentSha256 =
        Convert.ToHexString(SHA256.HashData(_content)).ToLowerInvariant();

    private static MalwareScanIdentity TestIdentity(DateTimeOffset now) =>
        new(true, "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));

    [Fact]
    public async Task InitialUpload_WhenArchiveSafe_ShouldPersistObservedProofAndAdvanceToMalwareScan()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await SeedPendingInspection(db);
        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.ARCHIVE_INSPECTION,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new ArchiveInspectionPayload());
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);

        ArchiveInspectionJobHandler handler = CreateArchiveHandler(
            db, AdmittedReportTestEvidence.BuildChain().PolicyDigest, fileCount: 3, totalExpandedBytes: 128);

        AssetProcessingJobOutcome outcome = await handler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                claimed.JobId, claimed.LeaseToken, claimed.AssetId, claimed.AssetVersionId,
                claimed.DefinitionVersion, claimed.AttemptCount, claimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None),
            CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();

        SecurityEvidenceRecord evidence = await db.SecurityEvidenceRecords.AsNoTracking()
            .SingleAsync(e => e.AssetVersionId == seed.Version.Id && e.EvidenceKind == SecurityEvidenceKind.ARCHIVE_VALIDATION);
        evidence.ContentSha256.Should().Be(_contentSha256);
        evidence.ScannedByteCount.Should().Be(_content.Length);
        evidence.ScopeComplete.Should().BeTrue();
        evidence.ImplementationIdentity.Should().Be(ArchiveSafetyInspector.IMPLEMENTATION_IDENTITY);
        evidence.EngineIdentity.Should().BeNull();
        evidence.JobId.Should().Be(claimed.JobId);
        evidence.Generation.Should().Be(1);
        evidence.SecurityRecheckId.Should().BeNull();
        evidence.Outcome.Should().Be(SecurityEvidenceOutcome.VALID);

        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).ProcessingStatus
            .Should().Be(AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        (await db.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.AssetVersionId == seed.Version.Id && j.Type == AssetProcessingJobType.MALWARE_SCAN))
            .Should().Be(1);
    }

    [Fact]
    public async Task InitialUpload_WhenMalwareClean_ShouldAtomicallyCommitReadyProofRunJobAndNotification()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedPendingInspection(db);
        AssetProcessingLifecycleStore lifecycle = CreateLifecycleStore(db, chain.PolicyDigest);

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.ARCHIVE_INSPECTION,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new ArchiveInspectionPayload());
        ClaimedAssetProcessingJob archiveClaimed = await ClaimOne(jobStore);

        ArchiveInspectionJobHandler archiveHandler = CreateArchiveHandler(db, chain.PolicyDigest, fileCount: 3, totalExpandedBytes: 128);
        await archiveHandler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                archiveClaimed.JobId, archiveClaimed.LeaseToken, archiveClaimed.AssetId, archiveClaimed.AssetVersionId,
                archiveClaimed.DefinitionVersion, archiveClaimed.AttemptCount, archiveClaimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None),
            CancellationToken.None);

        ClaimedAssetProcessingJob malwareClaimed = await ClaimOne(jobStore);
        malwareClaimed.Type.Should().Be(AssetProcessingJobType.MALWARE_SCAN);

        MalwareScanJobHandler malwareHandler = CreateMalwareHandler(db, chain.PolicyDigest, TestIdentity(DateTimeOffset.UtcNow));
        AssetProcessingJobOutcome outcome = await malwareHandler.Process(
            new AssetProcessingJobContext<MalwareScanPayload>(
                malwareClaimed.JobId, malwareClaimed.LeaseToken, malwareClaimed.AssetId, malwareClaimed.AssetVersionId,
                malwareClaimed.DefinitionVersion, malwareClaimed.AttemptCount, malwareClaimed.MaxAttempts,
                new MalwareScanPayload("1.0"), null, CancellationToken.None),
            CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();

        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id);
        version.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.READY);
        version.IsCurrent.Should().BeFalse();

        SecurityEvidenceRecord malware = await db.SecurityEvidenceRecords.AsNoTracking()
            .SingleAsync(e => e.AssetVersionId == seed.Version.Id && e.EvidenceKind == SecurityEvidenceKind.MALWARE_SCAN);
        malware.ContentSha256.Should().Be(_contentSha256);
        malware.ScannedByteCount.Should().Be(_content.Length);
        malware.ScopeComplete.Should().BeTrue();
        malware.IdentityAvailable.Should().BeTrue();
        malware.EngineIdentity.Should().Be("clamav");
        malware.EngineVersion.Should().Be("1.0.3");
        malware.SignatureIdentity.Should().Be("clamav 1.0.3/25425");
        malware.SignatureObservedAt.Should().NotBeNull();
        malware.SignatureValidUntil.Should().BeAfter(DateTimeOffset.UtcNow);
        malware.JobId.Should().Be(malwareClaimed.JobId);
        malware.Generation.Should().Be(1);
        malware.SecurityRecheckId.Should().BeNull();
        malware.Outcome.Should().Be(SecurityEvidenceOutcome.CLEAN);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync();
        run.ArchiveEvidenceId.Should().NotBe(Guid.Empty);
        run.MalwareEvidenceId.Should().Be(malware.Id);
        run.SecurityGeneration.Should().Be(1);
        run.SecurityRecheckId.Should().BeNull();
        run.State.Should().Be(CodeAnalysisRunState.QUEUED);
        run.InputDigest.Should().HaveLength(64);

        AssetProcessingJob analysisJob = await db.AssetProcessingJobs.AsNoTracking()
            .SingleAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS);
        analysisJob.AnalysisRunId.Should().Be(run.Id);
        run.JobId.Should().Be(analysisJob.Id);

        (await db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(1);
        (await db.UserNotifications.AsNoTracking()
            .CountAsync(n => n.RecipientUserId == seed.Asset.AuthorId)).Should().Be(1);

        // A replay of the same CLEAN commits without duplicating any effect.
        var replayed = await lifecycle.TransitionMalwareScanClean(
            malwareClaimed.JobId, malwareClaimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true),
            cancellationToken: CancellationToken.None);
        replayed.Should().BeTrue();
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);
        (await db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task EnqueueForInitialUpload_WhenIdentityUnavailableAndRequired_ShouldThrowWithoutWrites()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await SeedPendingInspection(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, AdmittedReportTestEvidence.BuildChain().PolicyDigest);

        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, true, false, null, null, null, null, null);

        Func<Task> act = () => enqueuer.EnqueueForInitialUpload(
            seed.Asset.Id, seed.Version.Id, _contentSha256, 1, evidence, Guid.NewGuid());

        await act.Should().ThrowAsync<SecurityEvidenceIncompleteException>();
        (await db.SecurityEvidenceRecords.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EnqueueForInitialUpload_WhenScopePartial_ShouldFailClosedWithoutWrites()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await SeedPendingInspection(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, AdmittedReportTestEvidence.BuildChain().PolicyDigest);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, false, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));

        InitialUploadProofResult result = await enqueuer.EnqueueForInitialUpload(
            seed.Asset.Id, seed.Version.Id, _contentSha256, 1, evidence, Guid.NewGuid());

        result.EvidencePersisted.Should().BeFalse();
        (await db.SecurityEvidenceRecords.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MalwareHandler_WhenObservedHashMismatch_ShouldRejectWithoutProofOrScheduling()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await SeedPendingMalwareScan(db);
        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);

        // The stored version advertises a different hash than the bytes actually scanned.
        await db.AssetVersions.Where(v => v.Id == seed.Version.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ContentSha256, new string('f', 64)));

        MalwareScanJobHandler handler = CreateMalwareHandler(
            db, AdmittedReportTestEvidence.BuildChain().PolicyDigest, TestIdentity(DateTimeOffset.UtcNow));
        AssetProcessingJobOutcome outcome = await handler.Process(
            new AssetProcessingJobContext<MalwareScanPayload>(
                claimed.JobId, claimed.LeaseToken, claimed.AssetId, claimed.AssetVersionId,
                claimed.DefinitionVersion, claimed.AttemptCount, claimed.MaxAttempts,
                new MalwareScanPayload("1.0"), null, CancellationToken.None),
            CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.FAILED);
        (await db.SecurityEvidenceRecords.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).ProcessingStatus
            .Should().Be(AssetVersionProcessingStatus.REJECTED);
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenFaultBeforeCommit_ShouldRollBackProofReadyRunAndJob()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        seedDb.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedPendingMalwareScanWithArchiveProof(seedDb);
        AssetProcessingJobStore jobStore = CreateJobStore(seedDb);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);

        var fault = new RunInsertFaultInterceptor();
        await using ApplicationDbContext faultDb = fixture.CreateDbContext(o => o.AddInterceptors(fault));
        AssetProcessingLifecycleStore lifecycle = CreateLifecycleStore(faultDb, chain.PolicyDigest);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, true, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));

        Func<Task> act = () => lifecycle.TransitionMalwareScanClean(
            claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using ApplicationDbContext verifierDb = fixture.CreateDbContext();
        (await verifierDb.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).ProcessingStatus
            .Should().Be(AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        AssetProcessingJob job = await verifierDb.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.RUNNING);
        job.LeaseToken.Should().Be(claimed.LeaseToken);
        (await verifierDb.SecurityEvidenceRecords.AsNoTracking()
            .CountAsync(e => e.AssetVersionId == seed.Version.Id && e.EvidenceKind == SecurityEvidenceKind.MALWARE_SCAN))
            .Should().Be(0);
        (await verifierDb.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        (await verifierDb.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);

        // The same lease retries cleanly once the fault is gone: nothing was stranded.
        await using ApplicationDbContext retryDb = fixture.CreateDbContext();
        AssetProcessingLifecycleStore retryLifecycle = CreateLifecycleStore(retryDb, chain.PolicyDigest);
        (await retryLifecycle.TransitionMalwareScanClean(
            claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None))
            .Should().BeTrue();
        (await verifierDb.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenArchiveProofHasNoProducingJob_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        seedDb.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedPendingMalwareScanWithArchiveProof(seedDb);

        // The archive proof row survives, but the job link that made it observed proof is gone,
        // so no producer can ever be named for it.
        await seedDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_evidence_records SET "JobId" = NULL WHERE "AssetVersionId" = {seed.Version.Id}
            """);

        AssetProcessingJobStore jobStore = CreateJobStore(seedDb);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);

        AssetProcessingLifecycleStore lifecycle = CreateLifecycleStore(seedDb, chain.PolicyDigest);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, true, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));

        (await lifecycle.TransitionMalwareScanClean(
            claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None))
            .Should().BeTrue();

        // READY still commits with the observed proof, but analysis is never scheduled for proof
        // no producer observed: no run, no analysis job and no scheduling intent can ever make
        // that proof admissible.
        await using ApplicationDbContext verifierDb = fixture.CreateDbContext();
        (await verifierDb.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).ProcessingStatus
            .Should().Be(AssetVersionProcessingStatus.READY);
        (await verifierDb.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        (await verifierDb.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(0);
        (await verifierDb.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.Type == OutboxMessageTypes.ANALYSIS_SCHEDULING)).Should().Be(0);
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenConcurrentDuplicates_ShouldCommitOneEffectSet()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        seedDb.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedPendingMalwareScanWithArchiveProof(seedDb);
        AssetProcessingJobStore jobStore = CreateJobStore(seedDb);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, true, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));

        await using ApplicationDbContext firstDb = fixture.CreateDbContext();
        await using ApplicationDbContext secondDb = fixture.CreateDbContext();
        Task<bool> first = CreateLifecycleStore(firstDb, chain.PolicyDigest).TransitionMalwareScanClean(
            claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None);
        Task<bool> second = CreateLifecycleStore(secondDb, chain.PolicyDigest).TransitionMalwareScanClean(
            claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None);
        await Task.WhenAll(first, second);

        (await first).Should().BeTrue();
        (await second).Should().BeTrue();
        (await seedDb.SecurityEvidenceRecords.AsNoTracking()
            .CountAsync(e => e.AssetVersionId == seed.Version.Id && e.EvidenceKind == SecurityEvidenceKind.MALWARE_SCAN))
            .Should().Be(1);
        (await seedDb.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);
        (await seedDb.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(1);
        (await seedDb.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenLeaseStale_ShouldLeaveEverythingUntouched()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedPendingMalwareScanWithArchiveProof(db);
        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);
        AssetProcessingLifecycleStore lifecycle = CreateLifecycleStore(db, chain.PolicyDigest);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, true, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));

        (await lifecycle.TransitionMalwareScanClean(
            claimed.JobId, Guid.NewGuid(), seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None))
            .Should().BeFalse();

        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).ProcessingStatus
            .Should().Be(AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        (await db.SecurityEvidenceRecords.AsNoTracking()
            .CountAsync(e => e.AssetVersionId == seed.Version.Id && e.EvidenceKind == SecurityEvidenceKind.MALWARE_SCAN))
            .Should().Be(0);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId)).Status
            .Should().Be(AssetProcessingJobStatus.RUNNING);
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenPinnedPolicyMissing_ShouldPreserveReadyProofWithoutFabrication()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await SeedPendingMalwareScanWithArchiveProof(db);
        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);
        AssetProcessingLifecycleStore lifecycle = CreateLifecycleStore(db, AdmittedReportTestEvidence.BuildChain().PolicyDigest);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, true, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));

        (await lifecycle.TransitionMalwareScanClean(
            claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None))
            .Should().BeTrue();

        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).ProcessingStatus
            .Should().Be(AssetVersionProcessingStatus.READY);
        (await db.SecurityEvidenceRecords.AsNoTracking()
            .CountAsync(e => e.AssetVersionId == seed.Version.Id && e.EvidenceKind == SecurityEvidenceKind.MALWARE_SCAN))
            .Should().Be(1);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);

        // A durable scheduling intent commits with the proof so restoring the policy recovers
        // exactly one run/job instead of stranding READY.
        OutboxMessage intent = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Type == OutboxMessageTypes.ANALYSIS_SCHEDULING);
        JsonSerializerOptions intentJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        AnalysisSchedulingPayload parsed = JsonSerializer.Deserialize<AnalysisSchedulingPayload>(intent.Payload, intentJson)!;
        parsed.AssetVersionId.Should().Be(seed.Version.Id);
        parsed.MalwareEvidenceId.Should().NotBe(Guid.Empty);
        parsed.ArchiveEvidenceId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task RecheckRouting_WhenJobsAreScoped_ShouldFlowThroughDedicatedFinalizers()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedReady(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, chain.PolicyDigest);
        SecurityProvenanceStore provenance = CreateProvenance(db, enqueuer);

        SecurityRecheckStartResult started = await provenance.StartSecurityRecheck(
            new SecurityRecheckStartRequest(
                seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1,
                ProductDigests.BuildRecheckOperationDigest(seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1),
                Guid.NewGuid()));
        started.SecurityGeneration.Should().Be(1);

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        ClaimedAssetProcessingJob archiveClaimed = await ClaimOne(jobStore);
        archiveClaimed.Type.Should().Be(AssetProcessingJobType.ARCHIVE_INSPECTION);
        archiveClaimed.SecurityRecheckId.Should().Be(started.RecheckId);

        ArchiveInspectionJobHandler archiveHandler = CreateArchiveHandler(db, chain.PolicyDigest, fileCount: 3, totalExpandedBytes: 128);
        AssetProcessingJobOutcome archiveOutcome = await archiveHandler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                archiveClaimed.JobId, archiveClaimed.LeaseToken, archiveClaimed.AssetId, archiveClaimed.AssetVersionId,
                archiveClaimed.DefinitionVersion, archiveClaimed.AttemptCount, archiveClaimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);
        archiveOutcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();

        SecurityRecheck recheck = await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.Id == started.RecheckId);
        recheck.State.Should().Be(SecurityRecheckState.SCANNING);
        SecurityEvidenceRecord archiveEvidence = await db.SecurityEvidenceRecords.AsNoTracking()
            .SingleAsync(e => e.SecurityRecheckId == started.RecheckId && e.EvidenceKind == SecurityEvidenceKind.ARCHIVE_VALIDATION);
        archiveEvidence.ContentSha256.Should().Be(_contentSha256);
        archiveEvidence.ScannedByteCount.Should().Be(_content.Length);
        archiveEvidence.JobId.Should().Be(archiveClaimed.JobId);

        ClaimedAssetProcessingJob malwareClaimed = await ClaimOne(jobStore);
        malwareClaimed.Type.Should().Be(AssetProcessingJobType.MALWARE_SCAN);
        malwareClaimed.SecurityRecheckId.Should().Be(started.RecheckId);

        MalwareScanJobHandler malwareHandler = CreateMalwareHandler(db, chain.PolicyDigest, TestIdentity(DateTimeOffset.UtcNow));
        AssetProcessingJobOutcome malwareOutcome = await malwareHandler.Process(
            new AssetProcessingJobContext<MalwareScanPayload>(
                malwareClaimed.JobId, malwareClaimed.LeaseToken, malwareClaimed.AssetId, malwareClaimed.AssetVersionId,
                malwareClaimed.DefinitionVersion, malwareClaimed.AttemptCount, malwareClaimed.MaxAttempts,
                new MalwareScanPayload("1.0"), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);
        malwareOutcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();

        SecurityRecheck completed = await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.Id == started.RecheckId);
        completed.State.Should().Be(SecurityRecheckState.SUCCEEDED);
        SecurityEvidenceRecord malwareEvidence = await db.SecurityEvidenceRecords.AsNoTracking()
            .SingleAsync(e => e.SecurityRecheckId == started.RecheckId && e.EvidenceKind == SecurityEvidenceKind.MALWARE_SCAN);
        malwareEvidence.Outcome.Should().Be(SecurityEvidenceOutcome.CLEAN);
        malwareEvidence.JobId.Should().Be(malwareClaimed.JobId);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync();
        run.SecurityRecheckId.Should().Be(started.RecheckId);
        run.SecurityGeneration.Should().Be(1);
        run.ArchiveEvidenceId.Should().Be(archiveEvidence.Id);
        run.MalwareEvidenceId.Should().Be(malwareEvidence.Id);

        // The READY version itself is never rewritten by its recheck and no initial-upload
        // evidence is fabricated alongside the scoped generation.
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).ProcessingStatus
            .Should().Be(AssetVersionProcessingStatus.READY);
        (await db.SecurityEvidenceRecords.AsNoTracking()
            .CountAsync(e => e.AssetVersionId == seed.Version.Id && e.SecurityRecheckId == null))
            .Should().Be(0);
    }

    [Fact]
    public async Task Recheck_WhenInfected_ShouldQuarantineVersionAndDenyBuyerAccess()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedReady(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, chain.PolicyDigest);
        SecurityProvenanceStore provenance = CreateProvenance(db, enqueuer);

        SecurityRecheckStartResult started = await provenance.StartSecurityRecheck(
            new SecurityRecheckStartRequest(
                seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1,
                ProductDigests.BuildRecheckOperationDigest(seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1),
                Guid.NewGuid()));

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        ClaimedAssetProcessingJob archiveClaimed = await ClaimOne(jobStore);
        ArchiveInspectionJobHandler archiveHandler = CreateArchiveHandler(db, chain.PolicyDigest, fileCount: 3, totalExpandedBytes: 128);
        await archiveHandler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                archiveClaimed.JobId, archiveClaimed.LeaseToken, archiveClaimed.AssetId, archiveClaimed.AssetVersionId,
                archiveClaimed.DefinitionVersion, archiveClaimed.AttemptCount, archiveClaimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        ClaimedAssetProcessingJob malwareClaimed = await ClaimOne(jobStore);
        MalwareScanJobHandler malwareHandler = CreateInfectedMalwareHandler(db, chain.PolicyDigest);
        AssetProcessingJobOutcome outcome = await malwareHandler.Process(
            new AssetProcessingJobContext<MalwareScanPayload>(
                malwareClaimed.JobId, malwareClaimed.LeaseToken, malwareClaimed.AssetId, malwareClaimed.AssetVersionId,
                malwareClaimed.DefinitionVersion, malwareClaimed.AttemptCount, malwareClaimed.MaxAttempts,
                new MalwareScanPayload("1.0"), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.FAILED);

        (await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.Id == started.RecheckId)).State
            .Should().Be(SecurityRecheckState.INFECTED);
        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id);
        version.SafetyRestriction.Should().Be(AssetVersionSafetyRestriction.INFECTED);
        (await PublicationEligibilityQuery.BuyerAccessibleVersions(db, seed.Asset.Id, 1).ToListAsync()).Should().BeEmpty();
        (await PublicationEligibilityQuery.SaleEligibleSnapshot(db, seed.Asset.Id, seed.Version.Id).ToListAsync()).Should().BeEmpty();
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Recheck_WhenIdentityUnavailable_ShouldErrorWithoutQuarantineOrAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedReady(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, chain.PolicyDigest);
        SecurityProvenanceStore provenance = CreateProvenance(db, enqueuer);

        SecurityRecheckStartResult started = await provenance.StartSecurityRecheck(
            new SecurityRecheckStartRequest(
                seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1,
                ProductDigests.BuildRecheckOperationDigest(seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1),
                Guid.NewGuid()));

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        ClaimedAssetProcessingJob archiveClaimed = await ClaimOne(jobStore);
        ArchiveInspectionJobHandler archiveHandler = CreateArchiveHandler(db, chain.PolicyDigest, fileCount: 3, totalExpandedBytes: 128);
        await archiveHandler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                archiveClaimed.JobId, archiveClaimed.LeaseToken, archiveClaimed.AssetId, archiveClaimed.AssetVersionId,
                archiveClaimed.DefinitionVersion, archiveClaimed.AttemptCount, archiveClaimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        ClaimedAssetProcessingJob malwareClaimed = await ClaimOne(jobStore);
        MalwareScanJobHandler malwareHandler = CreateMalwareHandler(db, chain.PolicyDigest, MalwareScanIdentity.Unavailable());
        AssetProcessingJobOutcome outcome = await malwareHandler.Process(
            new AssetProcessingJobContext<MalwareScanPayload>(
                malwareClaimed.JobId, malwareClaimed.LeaseToken, malwareClaimed.AssetId, malwareClaimed.AssetVersionId,
                malwareClaimed.DefinitionVersion, malwareClaimed.AttemptCount, malwareClaimed.MaxAttempts,
                new MalwareScanPayload("1.0"), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        // Operational ERROR: no quarantine, no analysis, the READY version is untouched.
        (await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.Id == started.RecheckId)).State
            .Should().Be(SecurityRecheckState.ERROR);
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SafetyRestriction
            .Should().Be(AssetVersionSafetyRestriction.NONE);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
    }

    [Fact]
    public async Task Recheck_WhenArchiveDecryptFails_ShouldQuarantineIntegrityMismatch()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedReady(db);

        User buyer = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(buyer);
        await db.SaveChangesAsync();
        Purchase purchase = TestData.CreatePurchase(buyer.Id, seed.Asset.Id, seed.Version.Id);
        TestData.AddCompletedPurchase(db, purchase, "Proof subject", seed.Asset.AuthorId);
        await db.SaveChangesAsync();

        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, chain.PolicyDigest);
        SecurityProvenanceStore provenance = CreateProvenance(db, enqueuer);
        SecurityRecheckStartResult started = await provenance.StartSecurityRecheck(
            new SecurityRecheckStartRequest(
                seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1,
                ProductDigests.BuildRecheckOperationDigest(seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1),
                Guid.NewGuid()));

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);
        ArchiveInspectionJobHandler handler = CreateAeadFailingArchiveHandler(db, chain.PolicyDigest);
        AssetProcessingJobOutcome outcome = await handler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                claimed.JobId, claimed.LeaseToken, claimed.AssetId, claimed.AssetVersionId,
                claimed.DefinitionVersion, claimed.AttemptCount, claimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.FAILED);

        // Truthful integrity identity: UNSAFE state with an integrity-mismatch restriction,
        // never INFECTED and never a silent operational error.
        (await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.Id == started.RecheckId)).State
            .Should().Be(SecurityRecheckState.UNSAFE);
        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id);
        version.SafetyRestriction.Should().Be(AssetVersionSafetyRestriction.INTEGRITY_MISMATCH);
        version.SafetyRestrictionReason.Should().Be("ARCHIVE_CORRUPT");
        version.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.READY);

        (await PublicationEligibilityQuery.BuyerAccessibleVersions(db, seed.Asset.Id, 1).ToListAsync()).Should().BeEmpty();
        (await PublicationEligibilityQuery.SaleEligibleSnapshot(db, seed.Asset.Id, seed.Version.Id).ToListAsync()).Should().BeEmpty();
        (await PublicationEligibilityQuery.OwnerSafePreviewVersions(db, seed.Asset.Id, seed.Version.Id).ToListAsync()).Should().BeEmpty();

        // Entitlement and history are preserved; only byte access is denied.
        (await db.Purchases.AsNoTracking().CountAsync(p => p.Id == purchase.Id)).Should().Be(1);
        (await db.AssetVersions.AsNoTracking().CountAsync(v => v.Id == seed.Version.Id)).Should().Be(1);
    }

    [Fact]
    public async Task Recheck_WhenMalwareDecryptFails_ShouldQuarantineIntegrityMismatch()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedReady(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, chain.PolicyDigest);
        SecurityProvenanceStore provenance = CreateProvenance(db, enqueuer);
        SecurityRecheckStartResult started = await provenance.StartSecurityRecheck(
            new SecurityRecheckStartRequest(
                seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1,
                ProductDigests.BuildRecheckOperationDigest(seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1),
                Guid.NewGuid()));

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        ClaimedAssetProcessingJob archiveClaimed = await ClaimOne(jobStore);
        ArchiveInspectionJobHandler archiveHandler = CreateArchiveHandler(db, chain.PolicyDigest, fileCount: 3, totalExpandedBytes: 128);
        await archiveHandler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                archiveClaimed.JobId, archiveClaimed.LeaseToken, archiveClaimed.AssetId, archiveClaimed.AssetVersionId,
                archiveClaimed.DefinitionVersion, archiveClaimed.AttemptCount, archiveClaimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        ClaimedAssetProcessingJob malwareClaimed = await ClaimOne(jobStore);
        MalwareScanJobHandler malwareHandler = CreateAeadFailingMalwareHandler(db, chain.PolicyDigest);
        AssetProcessingJobOutcome outcome = await malwareHandler.Process(
            new AssetProcessingJobContext<MalwareScanPayload>(
                malwareClaimed.JobId, malwareClaimed.LeaseToken, malwareClaimed.AssetId, malwareClaimed.AssetVersionId,
                malwareClaimed.DefinitionVersion, malwareClaimed.AttemptCount, malwareClaimed.MaxAttempts,
                new MalwareScanPayload("1.0"), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.FAILED);
        (await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.Id == started.RecheckId)).State
            .Should().Be(SecurityRecheckState.UNSAFE);
        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id);
        version.SafetyRestriction.Should().Be(AssetVersionSafetyRestriction.INTEGRITY_MISMATCH);
        (await PublicationEligibilityQuery.BuyerAccessibleVersions(db, seed.Asset.Id, 1).ToListAsync()).Should().BeEmpty();
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Recheck_WhenIntegrityQuarantineLeaseStale_ShouldLeaveEverythingUntouched()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedReady(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, chain.PolicyDigest);
        SecurityProvenanceStore provenance = CreateProvenance(db, enqueuer);
        SecurityRecheckStartResult started = await provenance.StartSecurityRecheck(
            new SecurityRecheckStartRequest(
                seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1,
                ProductDigests.BuildRecheckOperationDigest(seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1),
                Guid.NewGuid()));

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);
        ArchiveInspectionJobHandler handler = CreateAeadFailingArchiveHandler(db, chain.PolicyDigest);
        AssetProcessingJobOutcome outcome = await handler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                claimed.JobId, Guid.NewGuid(), claimed.AssetId, claimed.AssetVersionId,
                claimed.DefinitionVersion, claimed.AttemptCount, claimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.RetryableFailure>();
        (await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.Id == started.RecheckId)).State
            .Should().Be(SecurityRecheckState.QUEUED);
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SafetyRestriction
            .Should().Be(AssetVersionSafetyRestriction.NONE);
    }

    [Fact]
    public async Task SchedulingIntent_WhenPolicyRestored_ShouldRecoverExactlyOneRun()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        SeedData seed = await SeedPendingMalwareScanWithArchiveProof(db);
        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);
        AssetProcessingLifecycleStore lifecycle = CreateLifecycleStore(db, AdmittedReportTestEvidence.BuildChain().PolicyDigest);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, true, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));
        (await lifecycle.TransitionMalwareScanClean(
            claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None))
            .Should().BeTrue();

        OutboxMessage intent = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Type == OutboxMessageTypes.ANALYSIS_SCHEDULING);

        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        await db.SaveChangesAsync();

        await using ApplicationDbContext recoveryDb = fixture.CreateDbContext();
        AnalysisSchedulingOutboxHandler recovery = CreateSchedulingHandler(recoveryDb, chain.PolicyDigest, enabled: true);
        await recovery.Handle(intent, CancellationToken.None);
        // Repeated dispatch stays idempotent: still exactly one bound run/job.
        await recovery.Handle(intent, CancellationToken.None);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync();
        run.SecurityRecheckId.Should().BeNull();
        run.MalwareEvidenceId.Should().NotBe(Guid.Empty);
        AssetProcessingJob analysisJob = await db.AssetProcessingJobs.AsNoTracking()
            .SingleAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS);
        analysisJob.AnalysisRunId.Should().Be(run.Id);
    }

    [Fact]
    public async Task SchedulingIntent_WhenConcurrentCleans_ShouldRecoverExactlyOneRun()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        SeedData seed = await SeedPendingMalwareScanWithArchiveProof(seedDb);
        AssetProcessingJobStore jobStore = CreateJobStore(seedDb);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, true, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));
        await using ApplicationDbContext firstDb = fixture.CreateDbContext();
        await using ApplicationDbContext secondDb = fixture.CreateDbContext();
        Task<bool> first = CreateLifecycleStore(firstDb, AdmittedReportTestEvidence.BuildChain().PolicyDigest)
            .TransitionMalwareScanClean(
                claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
                new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None);
        Task<bool> second = CreateLifecycleStore(secondDb, AdmittedReportTestEvidence.BuildChain().PolicyDigest)
            .TransitionMalwareScanClean(
                claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
                new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None);
        await Task.WhenAll(first, second);

        // Either interleaving is coherent: a true winner with an idempotent replay, or a true
        // winner with a cleanly fenced loser. The effect set is single either way.
        ((await first) || (await second)).Should().BeTrue();
        (await seedDb.SecurityEvidenceRecords.AsNoTracking()
            .CountAsync(e => e.AssetVersionId == seed.Version.Id && e.EvidenceKind == SecurityEvidenceKind.MALWARE_SCAN))
            .Should().Be(1);
        (await seedDb.OutboxMessages.AsNoTracking()
            .CountAsync(m => m.Type == OutboxMessageTypes.ANALYSIS_SCHEDULING)).Should().Be(1);

        seedDb.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        await seedDb.SaveChangesAsync();

        await using ApplicationDbContext recoveryDb = fixture.CreateDbContext();
        AnalysisSchedulingOutboxHandler recovery = CreateSchedulingHandler(recoveryDb, chain.PolicyDigest, enabled: true);
        foreach (OutboxMessage intent in await seedDb.OutboxMessages.AsNoTracking()
                     .Where(m => m.Type == OutboxMessageTypes.ANALYSIS_SCHEDULING).ToListAsync())
        {
            await recovery.Handle(intent, CancellationToken.None);
        }

        (await seedDb.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);
        (await seedDb.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(1);
    }

    [Fact]
    public async Task SchedulingIntent_WhenUnrecoverable_ShouldDenyOrRetryExplicitly()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        SeedData seed = await SeedPendingMalwareScanWithArchiveProof(db);
        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);
        AssetProcessingLifecycleStore lifecycle = CreateLifecycleStore(db, AdmittedReportTestEvidence.BuildChain().PolicyDigest);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, true, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));
        (await lifecycle.TransitionMalwareScanClean(
            claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None))
            .Should().BeTrue();
        OutboxMessage intent = await db.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Type == OutboxMessageTypes.ANALYSIS_SCHEDULING);

        await using ApplicationDbContext recoveryDb = fixture.CreateDbContext();

        // Pinned policy still absent: the dispatcher must retry later, surfaced as a throw.
        AnalysisSchedulingOutboxHandler missingPolicy = CreateSchedulingHandler(recoveryDb, chain.PolicyDigest, enabled: true);
        Func<Task> stillMissing = () => missingPolicy.Handle(intent, CancellationToken.None);
        await stillMissing.Should().ThrowAsync<InvalidOperationException>();

        // Deliberately disabled analysis schedules nothing and fabricates no policy.
        AnalysisSchedulingOutboxHandler disabled = CreateSchedulingHandler(recoveryDb, chain.PolicyDigest, enabled: false);
        Func<Task> notEnabled = () => disabled.Handle(intent, CancellationToken.None);
        await notEnabled.Should().ThrowAsync<InvalidOperationException>();

        // Stale proof (hash no longer advertised) and quarantined versions schedule nothing.
        JsonSerializerOptions intentJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        AnalysisSchedulingPayload parsed =
            JsonSerializer.Deserialize<AnalysisSchedulingPayload>(intent.Payload, intentJson)!;
        var stale = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = OutboxMessageTypes.ANALYSIS_SCHEDULING,
            Payload = JsonSerializer.Serialize(
                parsed with { ContentSha256 = new string('f', 64) }, intentJson),
            OccurredAt = DateTimeOffset.UtcNow
        };
        await CreateSchedulingHandler(recoveryDb, chain.PolicyDigest, enabled: true).Handle(stale, CancellationToken.None);

        await db.AssetVersions.Where(v => v.Id == seed.Version.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(v => v.SafetyRestriction, AssetVersionSafetyRestriction.INTEGRITY_MISMATCH)
                .SetProperty(v => v.SafetyRestrictionReason, "ARCHIVE_CORRUPT")
                .SetProperty(v => v.SafetyRestrictedAt, DateTimeOffset.UtcNow));
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        await db.SaveChangesAsync();
        await CreateSchedulingHandler(recoveryDb, chain.PolicyDigest, enabled: true).Handle(intent, CancellationToken.None);

        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);

        // Malformed envelopes never schedule.
        var malformed = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = OutboxMessageTypes.ANALYSIS_SCHEDULING,
            Payload = "not-json",
            OccurredAt = DateTimeOffset.UtcNow
        };
        Func<Task> badEnvelope = () => CreateSchedulingHandler(recoveryDb, chain.PolicyDigest, enabled: true)
            .Handle(malformed, CancellationToken.None);
        await badEnvelope.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenProductionPathWithoutProof_ShouldFailClosed()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await SeedPendingMalwareScan(db);
        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);

        // The registered production path always wires the enqueuer with analysis enabled, so a
        // proof-less call fails closed instead of committing a bare READY.
        var lifecycle = new AssetProcessingLifecycleStore(
            db,
            MsOptions.Create(new AssetProcessingOptions()),
            CreateEnqueuer(db, AdmittedReportTestEvidence.BuildChain().PolicyDigest),
            MsOptions.Create(new CodeAnalysisOptions { Enabled = true }));

        (await lifecycle.TransitionMalwareScanClean(
            claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true),
            cancellationToken: CancellationToken.None))
            .Should().BeFalse();

        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).ProcessingStatus
            .Should().Be(AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        (await db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TransitionMalwareScanClean_WhenArchiveProofMissing_ShouldFailClosedWithoutReady()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        // No archive proof row: whatever the archive step did, it left no verifiable proof.
        SeedData seed = await SeedPendingMalwareScan(db);
        AssetProcessingJobStore jobStore = CreateJobStore(db);
        await jobStore.Enqueue(
            seed.Asset.Id, seed.Version.Id, AssetProcessingJobType.MALWARE_SCAN,
            definitionVersion: 1, initialDelay: TimeSpan.Zero, payload: new MalwareScanPayload("1.0"));
        ClaimedAssetProcessingJob claimed = await ClaimOne(jobStore);
        AssetProcessingLifecycleStore lifecycle = CreateLifecycleStore(db, chain.PolicyDigest);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = new MalwareScanEvidence(
            _contentSha256, _content.Length, true, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now, now.AddDays(2));

        (await lifecycle.TransitionMalwareScanClean(
            claimed.JobId, claimed.LeaseToken, seed.Asset.Id, seed.Version.Id,
            new MalwareScanResult(IsClean: true), evidence, 1, CancellationToken.None))
            .Should().BeFalse();

        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).ProcessingStatus
            .Should().Be(AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        (await db.SecurityEvidenceRecords.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EnqueueForInitialUpload_WhenIdentityDeficient_ShouldThrowWithoutWrites()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await SeedPendingInspection(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, AdmittedReportTestEvidence.BuildChain().PolicyDigest);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        // Missing engine version, missing signature identity, future observation and expiry each
        // deny admission even though an identity claims to be available, and an identity that
        // could not be observed at all is never admitted either.
        MalwareScanEvidence[] deficient =
        [
            new(_contentSha256, _content.Length, true, true, "clamav", null, "clamav 1.0.3/25425", now, now.AddDays(2)),
            new(_contentSha256, _content.Length, true, true, "clamav", "1.0.3", null, now, now.AddDays(2)),
            new(_contentSha256, _content.Length, true, true, "clamav", "1.0.3", "clamav 1.0.3/25425", now.AddHours(1), now.AddDays(2)),
            new(_contentSha256, _content.Length, true, true, "clamav", "1.0.3", "clamav 1.0.3/25425", now.AddDays(-3), now.AddDays(-1)),
            new(_contentSha256, _content.Length, true, false, null, null, null, null, null)
        ];

        foreach (MalwareScanEvidence evidence in deficient)
        {
            Func<Task> act = () => enqueuer.EnqueueForInitialUpload(
                seed.Asset.Id, seed.Version.Id, _contentSha256, 1, evidence, Guid.NewGuid());
            await act.Should().ThrowAsync<SecurityEvidenceIncompleteException>();
        }

        (await db.SecurityEvidenceRecords.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Recheck_WhenIdentityDeficient_ShouldErrorWithoutQuarantineOrAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedReady(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, chain.PolicyDigest);
        SecurityProvenanceStore provenance = CreateProvenance(db, enqueuer);
        SecurityRecheckStartResult started = await provenance.StartSecurityRecheck(
            new SecurityRecheckStartRequest(
                seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1,
                ProductDigests.BuildRecheckOperationDigest(seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1),
                Guid.NewGuid()));

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        ClaimedAssetProcessingJob archiveClaimed = await ClaimOne(jobStore);
        ArchiveInspectionJobHandler archiveHandler = CreateArchiveHandler(db, chain.PolicyDigest, fileCount: 3, totalExpandedBytes: 128);
        await archiveHandler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                archiveClaimed.JobId, archiveClaimed.LeaseToken, archiveClaimed.AssetId, archiveClaimed.AssetVersionId,
                archiveClaimed.DefinitionVersion, archiveClaimed.AttemptCount, archiveClaimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        // Available identity but missing engine version: the shared bar denies admission
        // operationally (ERROR) without quarantine and without analysis.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        ClaimedAssetProcessingJob malwareClaimed = await ClaimOne(jobStore);
        SecurityRecheckTransitionResult denied = await provenance.TransitionRecheckMalwareClean(
            malwareClaimed.JobId,
            malwareClaimed.LeaseToken,
            seed.Asset.Id,
            seed.Version.Id,
            new MalwareScanEvidence(
                _contentSha256, _content.Length, true, true, "clamav", null, "clamav 1.0.3/25425", now, now.AddDays(2)));

        denied.Committed.Should().BeTrue();
        (await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.Id == started.RecheckId)).State
            .Should().Be(SecurityRecheckState.ERROR);
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SafetyRestriction
            .Should().Be(AssetVersionSafetyRestriction.NONE);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Recheck_WhenScannerIdentityIsUnavailable_ShouldErrorWithoutQuarantineOrAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedReady(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, chain.PolicyDigest);
        SecurityProvenanceStore provenance = CreateProvenance(db, enqueuer);
        SecurityRecheckStartResult started = await provenance.StartSecurityRecheck(
            new SecurityRecheckStartRequest(
                seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1,
                ProductDigests.BuildRecheckOperationDigest(seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1),
                Guid.NewGuid()));

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        ClaimedAssetProcessingJob archiveClaimed = await ClaimOne(jobStore);
        ArchiveInspectionJobHandler archiveHandler = CreateArchiveHandler(db, chain.PolicyDigest, fileCount: 3, totalExpandedBytes: 128);
        await archiveHandler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                archiveClaimed.JobId, archiveClaimed.LeaseToken, archiveClaimed.AssetId, archiveClaimed.AssetVersionId,
                archiveClaimed.DefinitionVersion, archiveClaimed.AttemptCount, archiveClaimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        // No deployment option can weaken this: a scan whose identity could not be observed
        // fails the recheck operationally, without quarantine and without an analysis run.
        ClaimedAssetProcessingJob malwareClaimed = await ClaimOne(jobStore);
        SecurityRecheckTransitionResult denied = await provenance.TransitionRecheckMalwareClean(
            malwareClaimed.JobId,
            malwareClaimed.LeaseToken,
            seed.Asset.Id,
            seed.Version.Id,
            new MalwareScanEvidence(
                _contentSha256, _content.Length, ScopeComplete: true, IdentityAvailable: false,
                EngineIdentity: null, EngineVersion: null, SignatureIdentity: null,
                SignatureObservedAt: null, SignatureValidUntil: null));

        denied.Committed.Should().BeTrue();
        (await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.Id == started.RecheckId)).State
            .Should().Be(SecurityRecheckState.ERROR);
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SafetyRestriction
            .Should().Be(AssetVersionSafetyRestriction.NONE);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(0);
    }

    [Fact]
    public async Task Recheck_WhenCompletionFaultsBeforeCommit_ShouldReplayToExactlyOneRunAndJobPair()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        seedDb.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedReady(seedDb);
        (SecurityRecheckStartResult started, ClaimedAssetProcessingJob malwareClaimed) recheck =
            await StartRecheckThroughArchiveAsync(seedDb, chain, seed);

        // The scheduling step faults before commit, so the whole recheck completion rolls back:
        // no evidence, no run, no intent and no report can survive half applied.
        var fault = new RunInsertFaultInterceptor();
        await using ApplicationDbContext faultDb = fixture.CreateDbContext(o => o.AddInterceptors(fault));
        SecurityProvenanceStore faulty = CreateProvenance(faultDb, CreateEnqueuer(faultDb, chain.PolicyDigest));
        MalwareScanEvidence evidence = ObservedScan();

        Func<Task> act = () => faulty.TransitionRecheckMalwareClean(
            recheck.malwareClaimed.JobId, recheck.malwareClaimed.LeaseToken,
            seed.Asset.Id, seed.Version.Id, evidence);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using ApplicationDbContext interruptedDb = fixture.CreateDbContext();
        (await interruptedDb.SecurityRechecks.AsNoTracking()
            .SingleAsync(r => r.Id == recheck.started.RecheckId)).State
            .Should().Be(SecurityRecheckState.SCANNING);
        (await interruptedDb.SecurityEvidenceRecords.AsNoTracking()
            .CountAsync(e => e.SecurityRecheckId == recheck.started.RecheckId)).Should().Be(1);
        (await interruptedDb.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        (await interruptedDb.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);

        // The same lease replays the interrupted transition to one committed effect set.
        await using ApplicationDbContext replayDb = fixture.CreateDbContext();
        SecurityProvenanceStore replay = CreateProvenance(replayDb, CreateEnqueuer(replayDb, chain.PolicyDigest));
        SecurityRecheckTransitionResult replayed = await replay.TransitionRecheckMalwareClean(
            recheck.malwareClaimed.JobId, recheck.malwareClaimed.LeaseToken,
            seed.Asset.Id, seed.Version.Id, evidence);

        replayed.Committed.Should().BeTrue();
        replayed.AnalysisRunId.Should().NotBe(Guid.Empty);

        await using ApplicationDbContext verifiedDb = fixture.CreateDbContext();
        (await verifiedDb.SecurityRechecks.AsNoTracking()
            .SingleAsync(r => r.Id == recheck.started.RecheckId)).State
            .Should().Be(SecurityRecheckState.SUCCEEDED);
        (await verifiedDb.SecurityEvidenceRecords.AsNoTracking()
            .CountAsync(e => e.SecurityRecheckId == recheck.started.RecheckId)).Should().Be(2);
        await AssertSingleBoundRunAndJob(verifiedDb);
        (await verifiedDb.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);

        // A second delivery of the already settled transition commits nothing at all: the lease
        // it presents belongs to a job that is no longer running, so no twin effect set exists.
        await using ApplicationDbContext duplicateDb = fixture.CreateDbContext();
        SecurityProvenanceStore duplicate = CreateProvenance(duplicateDb, CreateEnqueuer(duplicateDb, chain.PolicyDigest));
        SecurityRecheckTransitionResult replayedAgain = await duplicate.TransitionRecheckMalwareClean(
            recheck.malwareClaimed.JobId, recheck.malwareClaimed.LeaseToken,
            seed.Asset.Id, seed.Version.Id, evidence);

        replayedAgain.Committed.Should().BeFalse();
        await AssertSingleBoundRunAndJob(verifiedDb);
        (await duplicateDb.SecurityEvidenceRecords.AsNoTracking()
            .CountAsync(e => e.SecurityRecheckId == recheck.started.RecheckId)).Should().Be(2);
    }

    [Fact]
    public async Task Recheck_WhenAnOlderRunOwnsTheGeneration_ShouldRecoverExactlyOnePairFromItsIntent()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        seedDb.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        SeedData seed = await SeedReady(seedDb);

        // An older analysis run is still executing for this selection generation, so the
        // completed recheck cannot schedule its own pair yet. It is bound to real generation-1
        // proof exactly as a genuine earlier enqueue would be.
        AdmittedReportTestEvidence.Chain olderChain = AdmittedReportTestEvidence.BuildChain();
        await AdmittedReportTestEvidence.SeedObservedProofAsync(seedDb, seed.Version, olderChain);
        var selectionGeneration = (await seedDb.AssetVersions.AsNoTracking()
            .SingleAsync(v => v.Id == seed.Version.Id)).AnalysisSelectionGeneration;
        var olderRunId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await seedDb.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO code_analysis_runs (
                "Id", "AssetId", "AssetVersionId", "ContentSha256", "PolicyVersionId", "PolicyDigest",
                "DeclarationDigest", "MaterialDigest", "InputDigest", "SelectionGeneration",
                "SecurityGeneration", "ArchiveEvidenceId", "MalwareEvidenceId", "ServiceKey", "State",
                "CreatedAt", "UpdatedAt"
            ) VALUES (
                {olderRunId}, {seed.Asset.Id}, {seed.Version.Id}, {_contentSha256}, {chain.PolicyVersionId},
                {chain.PolicyDigest}, {AdmittedReportTestEvidence.NewHex64()}, {AdmittedReportTestEvidence.NewHex64()},
                {AdmittedReportTestEvidence.NewHex64()}, {selectionGeneration}, 1, {olderChain.ArchiveEvidenceId},
                {olderChain.MalwareEvidenceId}, {SERVICE_KEY}, 'RUNNING', {now}, {now}
            )
            """);

        (SecurityRecheckStartResult started, ClaimedAssetProcessingJob malwareClaimed) recheck =
            await StartRecheckThroughArchiveAsync(seedDb, chain, seed);

        await using ApplicationDbContext completionDb = fixture.CreateDbContext();
        SecurityProvenanceStore provenance = CreateProvenance(completionDb, CreateEnqueuer(completionDb, chain.PolicyDigest));
        SecurityRecheckTransitionResult completed = await provenance.TransitionRecheckMalwareClean(
            recheck.malwareClaimed.JobId, recheck.malwareClaimed.LeaseToken,
            seed.Asset.Id, seed.Version.Id, ObservedScan());

        completed.Committed.Should().BeTrue();
        completed.AnalysisRunId.Should().Be(Guid.Empty);

        // The completed proof commits with exactly one durable retryable intent and no run while
        // the older run still owns the generation.
        await using ApplicationDbContext intentDb = fixture.CreateDbContext();
        (await intentDb.SecurityRechecks.AsNoTracking()
            .SingleAsync(r => r.Id == recheck.started.RecheckId)).State
            .Should().Be(SecurityRecheckState.SUCCEEDED);
        (await intentDb.CodeAnalysisRuns.AsNoTracking()
            .CountAsync(r => r.Id != olderRunId)).Should().Be(0);
        OutboxMessage intent = await intentDb.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Type == OutboxMessageTypes.ANALYSIS_SCHEDULING);
        AnalysisSchedulingPayload payload = JsonSerializer.Deserialize<AnalysisSchedulingPayload>(
            intent.Payload, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })!;
        payload.SecurityRecheckId.Should().Be(recheck.started.RecheckId);

        // Once the older run settles, recovery completes exactly one bound pair for the intent.
        await intentDb.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE code_analysis_runs
            SET "State" = 'COMPLETED', "CompletedAt" = {DateTimeOffset.UtcNow}, "UpdatedAt" = {DateTimeOffset.UtcNow}
            WHERE "Id" = {olderRunId}
            """);

        AnalysisSchedulingOutboxHandler handler = new(
            intentDb,
            CreateEnqueuer(intentDb, chain.PolicyDigest),
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = SERVICE_KEY,
                PolicyKey = POLICY_KEY,
                PolicyDigest = chain.PolicyDigest
            }),
            NullLogger<AnalysisSchedulingOutboxHandler>.Instance);
        await handler.Handle(intent, CancellationToken.None);

        await AssertSingleBoundRunAndJob(intentDb);
        (await intentDb.CodeAnalysisRuns.AsNoTracking().CountAsync(r => r.Id == olderRunId)).Should().Be(1);
    }

    private static async Task AssertSingleBoundRunAndJob(ApplicationDbContext db)
    {
        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking()
            .Where(r => r.State == CodeAnalysisRunState.QUEUED)
            .SingleAsync();
        AssetProcessingJob job = await db.AssetProcessingJobs.AsNoTracking()
            .SingleAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS);

        run.JobId.Should().Be(job.Id);
        job.AnalysisRunId.Should().Be(run.Id);
        job.Status.Should().Be(AssetProcessingJobStatus.QUEUED);
    }

    private static MalwareScanEvidence ObservedScan()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new MalwareScanEvidence(
            _contentSha256, _content.Length, true, true,
            "clamav", "1.0.3", "clamav 1.0.3/25425", now.AddMinutes(-1), now.AddDays(2));
    }

    /// <summary>Starts a recheck and drives its archive step, leaving the malware job claimed.</summary>
    private static async Task<(SecurityRecheckStartResult Started, ClaimedAssetProcessingJob MalwareClaimed)>
        StartRecheckThroughArchiveAsync(
            ApplicationDbContext db,
            AdmittedReportTestEvidence.Chain chain,
            SeedData seed)
    {
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, chain.PolicyDigest);
        SecurityProvenanceStore provenance = CreateProvenance(db, enqueuer);
        SecurityRecheckStartResult started = await provenance.StartSecurityRecheck(
            new SecurityRecheckStartRequest(
                seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1,
                ProductDigests.BuildRecheckOperationDigest(seed.Asset.Id, seed.Version.Id, _contentSha256, "policy-v1", 1),
                Guid.NewGuid()));

        AssetProcessingJobStore jobStore = CreateJobStore(db);
        ClaimedAssetProcessingJob archiveClaimed = await ClaimOne(jobStore);
        ArchiveInspectionJobHandler archiveHandler = CreateArchiveHandler(
            db, chain.PolicyDigest, fileCount: 3, totalExpandedBytes: 128);
        await archiveHandler.Process(
            new AssetProcessingJobContext<ArchiveInspectionPayload>(
                archiveClaimed.JobId, archiveClaimed.LeaseToken, archiveClaimed.AssetId, archiveClaimed.AssetVersionId,
                archiveClaimed.DefinitionVersion, archiveClaimed.AttemptCount, archiveClaimed.MaxAttempts,
                new ArchiveInspectionPayload(), null, CancellationToken.None,
                SecurityRecheckId: started.RecheckId),
            CancellationToken.None);

        ClaimedAssetProcessingJob malwareClaimed = await ClaimOne(jobStore);
        return (started, malwareClaimed);
    }

    private sealed class RunInsertFaultInterceptor : DbCommandInterceptor
    {
        public override InterceptionResult<int> NonQueryExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
        {
            if (command.CommandText.Contains("INSERT INTO code_analysis_runs", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Injected proof fault.");
            }

            return base.NonQueryExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(NonQueryExecuting(command, eventData, result));
    }

    private sealed record SeedData(Asset Asset, AssetVersion Version);

    private static async Task<SeedData> SeedPendingInspection(ApplicationDbContext db)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Initial proof subject");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION);
        version.ContentLength = _content.Length;
        version.ContentSha256 = _contentSha256;
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        return new SeedData(asset, version);
    }

    private static async Task<SeedData> SeedPendingMalwareScan(ApplicationDbContext db)
    {
        SeedData seed = await SeedPendingInspection(db);
        await db.AssetVersions.Where(v => v.Id == seed.Version.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ProcessingStatus, AssetVersionProcessingStatus.PENDING_MALWARE_SCAN));
        db.ChangeTracker.Clear();
        return seed;
    }

    private static async Task<SeedData> SeedPendingMalwareScanWithArchiveProof(ApplicationDbContext db)
    {
        SeedData seed = await SeedPendingMalwareScan(db);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var evidence = new ArchiveValidationEvidence(
            _contentSha256, 3, 128, _content.Length, true, ArchiveSafetyInspector.IMPLEMENTATION_IDENTITY);
        var digest = ProductDigests.BuildArchiveEvidenceDigest(
            seed.Asset.Id, seed.Version.Id, evidence, 1, 1, "1.0", now);

        // The archive proof was observed by the archive inspection job that completed in its own
        // transaction, so the producer row exists and is already succeeded when the malware
        // transition verifies the current proof.
        var archiveJob = new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            Type = AssetProcessingJobType.ARCHIVE_INSPECTION,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.SUCCEEDED,
            Stage = "READY",
            AttemptCount = 0,
            MaxAttempts = 3,
            AvailableAt = now,
            StartedAt = now,
            CompletedAt = now,
            Payload = AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.ARCHIVE_INSPECTION, new ArchiveInspectionPayload()),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AssetProcessingJobs.Add(archiveJob);

        db.SecurityEvidenceRecords.Add(new SecurityEvidenceRecord
        {
            Id = Guid.NewGuid(),
            AssetId = seed.Asset.Id,
            AssetVersionId = seed.Version.Id,
            ContentSha256 = _contentSha256,
            JobId = archiveJob.Id,
            EvidenceKind = SecurityEvidenceKind.ARCHIVE_VALIDATION,
            Generation = 1,
            DefinitionVersion = 1,
            Outcome = SecurityEvidenceOutcome.VALID,
            SecurityPolicyVersion = "1.0",
            ImplementationIdentity = ArchiveSafetyInspector.IMPLEMENTATION_IDENTITY,
            IdentityAvailable = false,
            ScannedByteCount = _content.Length,
            ScannedEntryCount = 3,
            ScopeComplete = true,
            CompletedAt = now,
            EvidenceDigest = digest,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        return seed;
    }

    private static async Task<SeedData> SeedReady(ApplicationDbContext db)
    {
        SeedData seed = await SeedPendingInspection(db);
        await db.AssetVersions.Where(v => v.Id == seed.Version.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.ProcessingStatus, AssetVersionProcessingStatus.READY));
        db.ChangeTracker.Clear();
        return seed;
    }

    private static AssetProcessingJobStore CreateJobStore(ApplicationDbContext db) =>
        new(db, NullLogger<AssetProcessingJobStore>.Instance, MsOptions.Create(new AssetProcessingOptions()));

    private static AssetProcessingLifecycleStore CreateLifecycleStore(ApplicationDbContext db, string policyDigest) =>
        new(db, MsOptions.Create(new AssetProcessingOptions()), CreateEnqueuer(db, policyDigest));

    private static AnalysisRunEnqueuer CreateEnqueuer(ApplicationDbContext db, string policyDigest) =>
        new(db,
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = SERVICE_KEY,
                PolicyKey = POLICY_KEY,
                PolicyDigest = policyDigest
            }),
            MsOptions.Create(new AssetProcessingOptions()),
            NullLogger<AnalysisRunEnqueuer>.Instance);

    private static SecurityProvenanceStore CreateProvenance(ApplicationDbContext db, IAnalysisRunEnqueuer enqueuer) =>
        new(db,
            MsOptions.Create(new AssetProcessingOptions()),
            enqueuer,
            NullLogger<SecurityProvenanceStore>.Instance);

    private static async Task<ClaimedAssetProcessingJob> ClaimOne(AssetProcessingJobStore jobStore) =>
        (await jobStore.ClaimPendingBatch(1, TimeSpan.FromMinutes(5), "proof-worker")).Single();

    private static void MockVerifiedStorage(
        IAssetStorageService storage, IEncryptionService encryption, byte[] content)
    {
        encryption.Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<Stream>(1).WriteAsync(content, ci.Arg<CancellationToken>()).AsTask());
        storage.OpenRead(
                Arg.Any<string>(),
                Arg.Any<Func<Stream, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<Stream, CancellationToken, Task>>()(new MemoryStream(content), ci.Arg<CancellationToken>()));
    }

    private static AnalysisSchedulingOutboxHandler CreateSchedulingHandler(
        ApplicationDbContext db, string policyDigest, bool enabled) =>
        new(db,
            new AnalysisRunEnqueuer(
                db,
                MsOptions.Create(new CodeAnalysisOptions
                {
                    Enabled = enabled,
                    ServiceKey = SERVICE_KEY,
                    PolicyKey = POLICY_KEY,
                    PolicyDigest = policyDigest
                }),
                MsOptions.Create(new AssetProcessingOptions()),
                NullLogger<AnalysisRunEnqueuer>.Instance),
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = enabled,
                ServiceKey = SERVICE_KEY,
                PolicyKey = POLICY_KEY,
                PolicyDigest = policyDigest
            }),
            NullLogger<AnalysisSchedulingOutboxHandler>.Instance);

    private static ArchiveInspectionJobHandler CreateAeadFailingArchiveHandler(
        ApplicationDbContext db, string policyDigest)
    {
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        assetStore.GetVersion(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Guid assetId = ci.ArgAt<Guid>(0);
                Guid versionId = ci.ArgAt<Guid>(1);
                return Task.FromResult(db.AssetVersions.AsNoTracking()
                    .FirstOrDefault(v => v.AssetId == assetId && v.Id == versionId));
            });
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IEncryptionService encryption = Substitute.For<IEncryptionService>();
        encryption.Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new CryptographicException("AEAD authentication failed.")));
        storage.OpenRead(
                Arg.Any<string>(),
                Arg.Any<Func<Stream, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<Stream, CancellationToken, Task>>()(new MemoryStream(_content), ci.Arg<CancellationToken>()));
        return new ArchiveInspectionJobHandler(
            assetStore, storage, encryption, Substitute.For<IArchiveSafetyInspector>(),
            new AssetProcessingLifecycleStore(
                db, MsOptions.Create(new AssetProcessingOptions()), CreateEnqueuer(db, policyDigest)),
            CreateProvenance(db, CreateEnqueuer(db, policyDigest)),
            MsOptions.Create(new AssetProcessingOptions()),
            NullLogger<ArchiveInspectionJobHandler>.Instance);
    }

    private static MalwareScanJobHandler CreateAeadFailingMalwareHandler(
        ApplicationDbContext db, string policyDigest)
    {
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        assetStore.GetVersion(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Guid assetId = ci.ArgAt<Guid>(0);
                Guid versionId = ci.ArgAt<Guid>(1);
                return Task.FromResult(db.AssetVersions.AsNoTracking()
                    .FirstOrDefault(v => v.AssetId == assetId && v.Id == versionId));
            });
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IEncryptionService encryption = Substitute.For<IEncryptionService>();
        encryption.Decrypt(Arg.Any<Stream>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException(new CryptographicException("AEAD authentication failed.")));
        storage.OpenRead(
                Arg.Any<string>(),
                Arg.Any<Func<Stream, CancellationToken, Task>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Func<Stream, CancellationToken, Task>>()(new MemoryStream(_content), ci.Arg<CancellationToken>()));
        return new MalwareScanJobHandler(
            assetStore, storage, encryption, Substitute.For<IContentMalwareScanner>(),
            new AssetProcessingLifecycleStore(
                db, MsOptions.Create(new AssetProcessingOptions()), CreateEnqueuer(db, policyDigest)),
            CreateProvenance(db, CreateEnqueuer(db, policyDigest)),
            MsOptions.Create(new AssetProcessingOptions()),
            NullLogger<MalwareScanJobHandler>.Instance);
    }

    private static ArchiveInspectionJobHandler CreateArchiveHandler(
        ApplicationDbContext db, string policyDigest, int fileCount, long totalExpandedBytes)
    {
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        assetStore.GetVersion(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Guid assetId = ci.ArgAt<Guid>(0);
                Guid versionId = ci.ArgAt<Guid>(1);
                return Task.FromResult(db.AssetVersions.AsNoTracking()
                    .FirstOrDefault(v => v.AssetId == assetId && v.Id == versionId));
            });
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IEncryptionService encryption = Substitute.For<IEncryptionService>();
        MockVerifiedStorage(storage, encryption, _content);
        IArchiveSafetyInspector inspector = Substitute.For<IArchiveSafetyInspector>();
        inspector.Inspect(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<Stream>().CopyTo(Stream.Null);
                return Task.FromResult(ArchiveSafetyResult.Safe(fileCount, totalExpandedBytes, "# Readme", null));
            });
        return new ArchiveInspectionJobHandler(
            assetStore, storage, encryption, inspector,
            new AssetProcessingLifecycleStore(
                db, MsOptions.Create(new AssetProcessingOptions()), CreateEnqueuer(db, policyDigest)),
            CreateProvenance(db, CreateEnqueuer(db, policyDigest)),
            MsOptions.Create(new AssetProcessingOptions()),
            NullLogger<ArchiveInspectionJobHandler>.Instance);
    }

    private static MalwareScanJobHandler CreateMalwareHandler(
        ApplicationDbContext db, string policyDigest, MalwareScanIdentity identity)
    {
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        assetStore.GetVersion(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Guid assetId = ci.ArgAt<Guid>(0);
                Guid versionId = ci.ArgAt<Guid>(1);
                return Task.FromResult(db.AssetVersions.AsNoTracking()
                    .FirstOrDefault(v => v.AssetId == assetId && v.Id == versionId));
            });
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IEncryptionService encryption = Substitute.For<IEncryptionService>();
        MockVerifiedStorage(storage, encryption, _content);
        IContentMalwareScanner scanner = Substitute.For<IContentMalwareScanner>();
        scanner.Scan(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<Stream>().CopyTo(Stream.Null);
                return Task.FromResult(MalwareScanOutcome.Clean(_content.Length, identity));
            });
        return new MalwareScanJobHandler(
            assetStore, storage, encryption, scanner,
            new AssetProcessingLifecycleStore(
                db, MsOptions.Create(new AssetProcessingOptions()), CreateEnqueuer(db, policyDigest)),
            CreateProvenance(db, CreateEnqueuer(db, policyDigest)),
            MsOptions.Create(new AssetProcessingOptions()),
            NullLogger<MalwareScanJobHandler>.Instance);
    }

    private static MalwareScanJobHandler CreateInfectedMalwareHandler(ApplicationDbContext db, string policyDigest)
    {
        IAssetStore assetStore = Substitute.For<IAssetStore>();
        assetStore.GetVersion(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                Guid assetId = ci.ArgAt<Guid>(0);
                Guid versionId = ci.ArgAt<Guid>(1);
                return Task.FromResult(db.AssetVersions.AsNoTracking()
                    .FirstOrDefault(v => v.AssetId == assetId && v.Id == versionId));
            });
        IAssetStorageService storage = Substitute.For<IAssetStorageService>();
        IEncryptionService encryption = Substitute.For<IEncryptionService>();
        MockVerifiedStorage(storage, encryption, _content);
        IContentMalwareScanner scanner = Substitute.For<IContentMalwareScanner>();
        scanner.Scan(Arg.Any<Stream>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                ci.Arg<Stream>().CopyTo(Stream.Null);
                return Task.FromResult(MalwareScanOutcome.Infected("Eicar-Test-Signature", _content.Length));
            });
        return new MalwareScanJobHandler(
            assetStore, storage, encryption, scanner,
            new AssetProcessingLifecycleStore(
                db, MsOptions.Create(new AssetProcessingOptions()), CreateEnqueuer(db, policyDigest)),
            CreateProvenance(db, CreateEnqueuer(db, policyDigest)),
            MsOptions.Create(new AssetProcessingOptions()),
            NullLogger<MalwareScanJobHandler>.Instance);
    }
}
