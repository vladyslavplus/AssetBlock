using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
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
/// Proves an analysis run is scheduled only for proof a concrete producer actually observed. The
/// producing job link is mandatory and has to resolve to the succeeded, correctly scoped, correctly
/// defined job of this exact version carrying the approved security policy in its own payload; the
/// scanner identity has to be fully observed; and the policy and definition a proof claims are the
/// persisted approved ones, never an arbitrary non-blank string. Every rejection schedules no run
/// and no analysis job, and a missing producer is impossible by foreign key rather than by trust.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class AnalysisSecurityProofProvenancePostgresTests(PostgresFixture fixture)
{
    private const string POLICY_KEY = "code-analysis-policy-v1";

    private static readonly byte[] _content = Enumerable.Range(0, 64).Select(i => (byte)i).ToArray();
    private static readonly string _contentSha256 =
        Convert.ToHexString(SHA256.HashData(_content)).ToLowerInvariant();

    private static string NewDigest() =>
        Convert.ToHexString(SHA256.HashData(Guid.NewGuid().ToByteArray())).ToLowerInvariant();

    [Fact]
    public async Task Enqueue_WhenProofRecordsNoProducingJob_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db);
        await SetEvidenceJob(db, seed.MalwareEvidenceId, jobId: null);

        AnalysisRunEnqueueResult enqueued = await CreateEnqueuer(db, seed.PolicyDigest).Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.PROOF_INVALID);
        await AssertNothingScheduled(db);
    }

    [Fact]
    public async Task Enqueue_WhenProducingJobBelongsToAnotherVersion_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db);

        Guid foreignJobId = await SeedForeignProducer(db, seed.AssetId);
        await SetEvidenceJob(db, seed.ArchiveEvidenceId, foreignJobId);

        AnalysisRunEnqueueResult enqueued = await CreateEnqueuer(db, seed.PolicyDigest).Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.PROOF_INVALID);
        await AssertNothingScheduled(db);
    }

    [Fact]
    public async Task Enqueue_WhenProducingJobIsOfTheWrongKind_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db);

        Guid malwareProducer = await ReadProducer(db, seed.VersionId, AssetProcessingJobType.MALWARE_SCAN);
        await SetEvidenceJob(db, seed.ArchiveEvidenceId, malwareProducer);

        AnalysisRunEnqueueResult enqueued = await CreateEnqueuer(db, seed.PolicyDigest).Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.PROOF_INVALID);
        await AssertNothingScheduled(db);
    }

    [Fact]
    public async Task Enqueue_WhenEvidenceDefinitionVersionIsArbitrary_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db);

        // The producers still carry definition 1, so an arbitrary positive definition on the
        // proof binds to no producer at all.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_evidence_records SET "DefinitionVersion" = 9 WHERE "Id" = {seed.MalwareEvidenceId}
            """);

        AnalysisRunEnqueueResult enqueued = await CreateEnqueuer(db, seed.PolicyDigest).Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.PROOF_INVALID);
        await AssertNothingScheduled(db);
    }

    [Fact]
    public async Task Enqueue_WhenProducingJobNeverSucceeded_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db, producerStatus: AssetProcessingJobStatus.FAILED);

        AnalysisRunEnqueueResult enqueued = await CreateEnqueuer(db, seed.PolicyDigest).Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.PROOF_INVALID);
        await AssertNothingScheduled(db);
    }

    [Fact]
    public async Task Enqueue_WhenProducingJobScopeBelongsToAnotherRecheck_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db);
        Guid foreignRecheckId = await SeedUnselectedRecheck(db, seed.AssetId, seed.VersionId);

        // Repoint both producers at a different recheck scope; the unscoped proof then names no
        // producer of its own.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs SET "SecurityRecheckId" = {foreignRecheckId}
            WHERE "AssetVersionId" = {seed.VersionId} AND "SecurityRecheckId" IS NULL
            """);

        AnalysisRunEnqueueResult enqueued = await CreateEnqueuer(db, seed.PolicyDigest).Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.PROOF_INVALID);
        await AssertNothingScheduled(db);
    }

    [Fact]
    public async Task Enqueue_WhenScannerJobRanUnderAnotherPolicy_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db, scannerPayloadPolicyVersion: "9.9");

        AnalysisRunEnqueueResult enqueued = await CreateEnqueuer(db, seed.PolicyDigest).Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.PROOF_INVALID);
        await AssertNothingScheduled(db);
    }

    [Fact]
    public async Task Enqueue_WhenEvidenceCarriesAnAlteredNonBlankPolicy_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db, evidencePolicyVersion: "policy-v9-altered");

        AnalysisRunEnqueueResult enqueued = await CreateEnqueuer(db, seed.PolicyDigest).Enqueue(
            seed.AssetId, seed.VersionId, null, seed.ArchiveEvidenceId, seed.MalwareEvidenceId,
            1, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.PROOF_INVALID);
        await AssertNothingScheduled(db);
    }

    [Fact]
    public async Task Enqueue_WhenRecheckProofCarriesAnotherDefinitionVersion_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db);

        // A completed generation-2 proof whose producers really ran definition 7 still cannot
        // admit analysis: the approved recheck identity pinned definition 1.
        RecheckProof recheck = await SeedSelectedRecheck(db, seed, definitionVersion: 7);

        AnalysisRunEnqueueResult enqueued = await CreateEnqueuer(db, seed.PolicyDigest).Enqueue(
            seed.AssetId, seed.VersionId, recheck.RecheckId, recheck.ArchiveEvidenceId,
            recheck.MalwareEvidenceId, 2, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.PROOF_INVALID);
        await AssertNothingScheduled(db);
    }

    [Fact]
    public async Task Enqueue_WhenRecheckProofCarriesAnotherPolicy_ShouldNotScheduleAnalysis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db);
        RecheckProof recheck = await SeedSelectedRecheck(db, seed, definitionVersion: 1);

        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_evidence_records
            SET "SecurityPolicyVersion" = 'policy-v9-altered'
            WHERE "Id" = {recheck.MalwareEvidenceId}
            """);

        AnalysisRunEnqueueResult enqueued = await CreateEnqueuer(db, seed.PolicyDigest).Enqueue(
            seed.AssetId, seed.VersionId, recheck.RecheckId, recheck.ArchiveEvidenceId,
            recheck.MalwareEvidenceId, 2, _contentSha256, DateTimeOffset.UtcNow);

        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.PROOF_INVALID);
        await AssertNothingScheduled(db);
    }

    [Fact]
    public async Task SecurityEvidenceRecords_WhenJobLinkWouldBeDangling_ShouldBeRefusedByTheForeignKey()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db);

        // A proof that references no producer at all cannot even be persisted: the foreign key,
        // not a later trust check, is what makes the missing-producer case unreachable.
        Func<Task> act = () => db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_evidence_records SET "JobId" = {Guid.NewGuid()} WHERE "Id" = {seed.MalwareEvidenceId}
            """);

        PostgresException failure = (await act.Should().ThrowAsync<PostgresException>()).Which;
        failure.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task EnqueueForInitialUpload_WhenScannerIdentityIsUnavailable_ShouldThrowWithoutWrites()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db);
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);

        MalwareScanEvidence unavailable = new(
            _contentSha256, _content.Length, ScopeComplete: true, IdentityAvailable: false,
            EngineIdentity: null, EngineVersion: null, SignatureIdentity: null,
            SignatureObservedAt: null, SignatureValidUntil: null);

        Func<Task> act = () => enqueuer.EnqueueForInitialUpload(
            seed.AssetId, seed.VersionId, _contentSha256, 1, unavailable, Guid.NewGuid());

        await act.Should().ThrowAsync<SecurityEvidenceIncompleteException>();
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task EnqueueForInitialUpload_WhenScannerJobPolicyDiffers_ShouldNotCreateAnalysisRun()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ProvenanceSeed seed = await SeedProof(db, scannerPayloadPolicyVersion: "9.9");
        IAnalysisRunEnqueuer enqueuer = CreateEnqueuer(db, seed.PolicyDigest);

        MalwareScanEvidence observed = new(
            _contentSha256, _content.Length, ScopeComplete: true, IdentityAvailable: true,
            EngineIdentity: "clamav", EngineVersion: "1.0.3", SignatureIdentity: "clamav 1.0.3/25425",

            // Observed strictly before the database clock is read, so host/container clock
            // alignment can never turn a coherent observation into an insufficient identity.
            SignatureObservedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            SignatureValidUntil: DateTimeOffset.UtcNow.AddDays(2));

        // The observed proof still persists, but the incoherent producer never yields a run, and
        // no scheduling intent pretends a permanently inadmissible proof could ever be retried.
        InitialUploadProofResult proof = await enqueuer.EnqueueForInitialUpload(
            seed.AssetId, seed.VersionId, _contentSha256, 1, observed, Guid.NewGuid());

        proof.EvidencePersisted.Should().BeTrue();
        proof.AnalysisRunId.Should().Be(Guid.Empty);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(0);
        (await db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(0);
    }

    private static async Task AssertNothingScheduled(ApplicationDbContext db)
    {
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.AssetProcessingJobs.AsNoTracking()
            .CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS)).Should().Be(0);
    }

    private static AnalysisRunEnqueuer CreateEnqueuer(ApplicationDbContext db, string policyDigest) =>
        new(db,
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = "code-analysis",
                PolicyKey = POLICY_KEY,
                PolicyDigest = policyDigest
            }),
            MsOptions.Create(new AssetProcessingOptions()),
            NullLogger<AnalysisRunEnqueuer>.Instance);

    private static async Task SetEvidenceJob(ApplicationDbContext db, Guid evidenceId, Guid? jobId) =>
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_evidence_records SET "JobId" = {jobId} WHERE "Id" = {evidenceId}
            """);

    private static async Task<Guid> ReadProducer(
        ApplicationDbContext db,
        Guid assetVersionId,
        AssetProcessingJobType type) =>
        await db.AssetProcessingJobs.AsNoTracking()
            .Where(j => j.AssetVersionId == assetVersionId && j.Type == type)
            .Select(j => j.Id)
            .SingleAsync();

    private static async Task<Guid> SeedForeignProducer(ApplicationDbContext db, Guid assetId)
    {
        // A second version of the same asset owns this producer, so the proof names a real job
        // that observed different bytes.
        AssetVersion version = TestData.CreateAssetVersion(
            assetId, versionNumber: 2, isCurrent: false, processingStatus: AssetVersionProcessingStatus.READY);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        db.AssetProcessingJobs.Add(ProducerJob(
            assetId, version.Id, AssetProcessingJobType.ARCHIVE_INSPECTION,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.ARCHIVE_INSPECTION, new ArchiveInspectionPayload()),
            securityRecheckId: null, definitionVersion: 1, status: AssetProcessingJobStatus.SUCCEEDED,
            now: DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();

        return await ReadProducer(db, version.Id, AssetProcessingJobType.ARCHIVE_INSPECTION);
    }

    private static async Task<Guid> SeedUnselectedRecheck(
        ApplicationDbContext db,
        Guid assetId,
        Guid assetVersionId)
    {
        var recheck = new SecurityRecheck
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            ContentSha256 = _contentSha256,
            SecurityGeneration = 3,
            SecurityPolicyVersion = "policy-v1",
            DefinitionVersion = 1,
            OperationDigest = NewDigest(),
            OperationReceiptId = Guid.NewGuid(),
            State = SecurityRecheckState.SUCCEEDED,
            SelectedGeneration = null,
            RequestedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        };
        db.SecurityRechecks.Add(recheck);
        await db.SaveChangesAsync();

        return recheck.Id;
    }

    private sealed record ProvenanceSeed(
        Guid AssetId,
        Guid VersionId,
        Guid ArchiveEvidenceId,
        Guid MalwareEvidenceId,
        string PolicyDigest);

    private sealed record RecheckProof(Guid RecheckId, Guid ArchiveEvidenceId, Guid MalwareEvidenceId);

    private static async Task<ProvenanceSeed> SeedProof(
        ApplicationDbContext db,
        AssetProcessingJobStatus producerStatus = AssetProcessingJobStatus.SUCCEEDED,
        string? evidencePolicyVersion = null,
        int evidenceDefinitionVersion = 1,
        int producerDefinitionVersion = 1,
        string? scannerPayloadPolicyVersion = null)
    {
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));

        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Proof provenance subject");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        version.ContentLength = _content.Length;
        version.ContentSha256 = _contentSha256;
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var policyVersion = evidencePolicyVersion ?? AssetProcessingDefaults.MALWARE_POLICY_VERSION;

        AssetProcessingJob archiveJob = ProducerJob(
            asset.Id, version.Id, AssetProcessingJobType.ARCHIVE_INSPECTION,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.ARCHIVE_INSPECTION, new ArchiveInspectionPayload()),
            securityRecheckId: null, producerDefinitionVersion, producerStatus, now);
        AssetProcessingJob malwareJob = ProducerJob(
            asset.Id, version.Id, AssetProcessingJobType.MALWARE_SCAN,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.MALWARE_SCAN,
                new MalwareScanPayload(scannerPayloadPolicyVersion ?? policyVersion)),
            securityRecheckId: null, producerDefinitionVersion, producerStatus, now);
        db.AssetProcessingJobs.AddRange(archiveJob, malwareJob);

        var archiveId = Guid.NewGuid();
        var malwareId = Guid.NewGuid();
        db.SecurityEvidenceRecords.AddRange(
            ArchiveEvidence(asset.Id, version.Id, archiveId, archiveJob.Id, policyVersion,
                evidenceDefinitionVersion, now),
            MalwareEvidence(asset.Id, version.Id, malwareId, malwareJob.Id, policyVersion,
                evidenceDefinitionVersion, now));
        await db.SaveChangesAsync();

        return new ProvenanceSeed(asset.Id, version.Id, archiveId, malwareId, chain.PolicyDigest);
    }

    /// <summary>A completed generation-2 proof observed by real recheck-scoped producers.</summary>
    private static async Task<RecheckProof> SeedSelectedRecheck(
        ApplicationDbContext db,
        ProvenanceSeed seed,
        int definitionVersion)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var recheckId = Guid.NewGuid();
        var archiveId = Guid.NewGuid();
        var malwareId = Guid.NewGuid();
        const string recheckPolicy = "policy-v1";

        db.SecurityRechecks.Add(new SecurityRecheck
        {
            Id = recheckId,
            AssetId = seed.AssetId,
            AssetVersionId = seed.VersionId,
            ContentSha256 = _contentSha256,
            SecurityGeneration = 2,
            SecurityPolicyVersion = recheckPolicy,
            DefinitionVersion = 1,
            OperationDigest = NewDigest(),
            OperationReceiptId = Guid.NewGuid(),
            State = SecurityRecheckState.SUCCEEDED,
            SelectedGeneration = 2,
            CompletedGeneration = 2,
            RequestedAt = now,
            CompletedAt = now
        });

        // The recheck row and its evidence reference each other, so they commit in two steps.
        await db.SaveChangesAsync();

        AssetProcessingJob archiveJob = ProducerJob(
            seed.AssetId, seed.VersionId, AssetProcessingJobType.ARCHIVE_INSPECTION,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.ARCHIVE_INSPECTION, new ArchiveInspectionPayload()),
            recheckId, definitionVersion, AssetProcessingJobStatus.SUCCEEDED, now);
        AssetProcessingJob malwareJob = ProducerJob(
            seed.AssetId, seed.VersionId, AssetProcessingJobType.MALWARE_SCAN,
            AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.MALWARE_SCAN, new MalwareScanPayload(recheckPolicy)),
            recheckId, definitionVersion, AssetProcessingJobStatus.SUCCEEDED, now);
        db.AssetProcessingJobs.AddRange(archiveJob, malwareJob);

        db.SecurityEvidenceRecords.AddRange(
            ArchiveEvidence(seed.AssetId, seed.VersionId, archiveId, archiveJob.Id, recheckPolicy,
                definitionVersion, now, recheckId, generation: 2),
            MalwareEvidence(seed.AssetId, seed.VersionId, malwareId, malwareJob.Id, recheckPolicy,
                definitionVersion, now, recheckId, generation: 2));
        await db.SaveChangesAsync();

        return new RecheckProof(recheckId, archiveId, malwareId);
    }

    private static AssetProcessingJob ProducerJob(
        Guid assetId,
        Guid assetVersionId,
        AssetProcessingJobType type,
        string payload,
        Guid? securityRecheckId,
        int definitionVersion,
        AssetProcessingJobStatus status,
        DateTimeOffset now)
    {
        var terminal = status is AssetProcessingJobStatus.SUCCEEDED
            or AssetProcessingJobStatus.FAILED
            or AssetProcessingJobStatus.CANCELLED;

        return new AssetProcessingJob
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            Type = type,
            DefinitionVersion = definitionVersion,
            Status = status,
            Stage = status.ToString(),
            AttemptCount = 0,
            MaxAttempts = 3,
            AvailableAt = now,
            StartedAt = status is AssetProcessingJobStatus.QUEUED ? null : now,
            CompletedAt = terminal ? now : null,
            Payload = payload,
            SecurityRecheckId = securityRecheckId,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static SecurityEvidenceRecord ArchiveEvidence(
        Guid assetId,
        Guid assetVersionId,
        Guid id,
        Guid jobId,
        string policyVersion,
        int definitionVersion,
        DateTimeOffset now,
        Guid? securityRecheckId = null,
        int generation = 1) =>
        new()
        {
            Id = id,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            ContentSha256 = _contentSha256,
            SecurityRecheckId = securityRecheckId,
            JobId = jobId,
            EvidenceKind = SecurityEvidenceKind.ARCHIVE_VALIDATION,
            Generation = generation,
            DefinitionVersion = definitionVersion,
            Outcome = SecurityEvidenceOutcome.VALID,
            SecurityPolicyVersion = policyVersion,
            ImplementationIdentity = "archive-inspector",
            IdentityAvailable = false,
            ScannedByteCount = _content.Length,
            ScannedEntryCount = 3,
            ScopeComplete = true,
            CompletedAt = now,
            EvidenceDigest = NewDigest(),
            CreatedAt = now
        };

    private static SecurityEvidenceRecord MalwareEvidence(
        Guid assetId,
        Guid assetVersionId,
        Guid id,
        Guid jobId,
        string policyVersion,
        int definitionVersion,
        DateTimeOffset now,
        Guid? securityRecheckId = null,
        int generation = 1) =>
        new()
        {
            Id = id,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            ContentSha256 = _contentSha256,
            SecurityRecheckId = securityRecheckId,
            JobId = jobId,
            EvidenceKind = SecurityEvidenceKind.MALWARE_SCAN,
            Generation = generation,
            DefinitionVersion = definitionVersion,
            Outcome = SecurityEvidenceOutcome.CLEAN,
            SecurityPolicyVersion = policyVersion,
            EngineIdentity = "clamav",
            EngineVersion = "1.0.3",
            SignatureIdentity = "clamav 1.0.3/25425",
            SignatureObservedAt = now.AddMinutes(-1),
            SignatureValidUntil = now.AddDays(2),
            IdentityAvailable = true,
            ScannedByteCount = _content.Length,
            ScopeComplete = true,
            CompletedAt = now,
            EvidenceDigest = NewDigest(),
            CreatedAt = now
        };
}
