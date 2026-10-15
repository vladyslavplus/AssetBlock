using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Proves new analysis claims require the full persisted readiness chain: stable capacity
/// registration, the exact pinned policy row speaking the admitted contract, and a registered
/// handler. Nonblank options alone never authorize a claim and no queued attempt is spent.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class AnalysisReadinessGatePostgresTests(PostgresFixture fixture)
{
    private const string SERVICE_KEY = "code-analysis";
    private const string POLICY_KEY = "code-analysis-policy-v1";
    private static readonly string _pinnedManifestDigest = AdmittedReportTestEvidence.RuntimeManifestDigest;

    [Fact]
    public async Task EnsureReady_WhenExactRowCapacityAndHandlerPresent_ShouldReturnTrue()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        await db.SaveChangesAsync();

        (await CreateGate(db, chain.PolicyDigest, capacity: 1, withHandler: true).EnsureReady()).Should().BeTrue();
    }

    [Fact]
    public async Task EnsureReady_WhenRuntimeManifestIsNotPinned_ShouldReturnFalse()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        await db.SaveChangesAsync();

        (await CreateGate(db, chain.PolicyDigest, capacity: 1, withHandler: true, runtimeManifestDigest: "")
            .EnsureReady()).Should().BeFalse();
    }

    [Theory]
    [InlineData("modelKey")]
    [InlineData("indexKey")]
    [InlineData("referenceSnapshotId")]
    [InlineData("verifierReason")]
    public async Task EnsureReady_WhenPersistedScalarIsAltered_ShouldReturnFalse(string scalar)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AlteredRow(chain, scalar));
        await db.SaveChangesAsync();

        // The policy key, the policy digest and the report contract are untouched, so only the
        // complete runtime-manifest digest can notice the altered approved identity.
        (await CreateGate(db, chain.PolicyDigest, capacity: 1, withHandler: true).EnsureReady()).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureReady_WhenPersistedStageMatrixIsIncomplete_ShouldReturnFalse()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(
            chain,
            stageMatrixJson: AdmittedReportTestEvidence.StageMatrixJson().Replace(
                """{"stage": "SEMANTIC", "applicability": "MANDATORY"},""",
                string.Empty,
                StringComparison.Ordinal)));
        await db.SaveChangesAsync();

        (await CreateGate(db, chain.PolicyDigest, capacity: 1, withHandler: true).EnsureReady()).Should().BeFalse();
    }

    [Fact]
    public async Task EnsureReady_WhenPersistedToolIdentityIsAltered_ShouldReturnFalse()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(
            chain,
            toolIdentitiesJson: AdmittedReportTestEvidence.ToolIdentitiesJson()
                .Replace("encoder-only", "encoder-other", StringComparison.Ordinal)));
        await db.SaveChangesAsync();

        (await CreateGate(db, chain.PolicyDigest, capacity: 1, withHandler: true).EnsureReady()).Should().BeFalse();
    }

    /// <summary>The approved row with exactly one approved-identity scalar changed.</summary>
    private static CodeAnalysisPolicyVersion AlteredRow(AdmittedReportTestEvidence.Chain chain, string scalar) =>
        scalar switch
        {
            "modelKey" => AdmittedReportTestEvidence.Policy(chain, modelKey: AdmittedReportTestEvidence.NewHex64()),
            "indexKey" => AdmittedReportTestEvidence.Policy(chain, indexKey: AdmittedReportTestEvidence.NewHex64()),
            "referenceSnapshotId" => AdmittedReportTestEvidence.Policy(chain, referenceSnapshotId: "snapshot-2026-11-02"),
            _ => AdmittedReportTestEvidence.Policy(
                chain,
                verifierReason: "Retrieval quality research evaluation has now been reviewed.")
        };

    [Fact]
    public async Task EnsureReady_WhenClaimsStayClosed_ShouldSpendNoAttemptAndStillFreeAnExpiredReservation()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await SeedVersionWithQueuedSecurityJob(db);
        await SeedQueuedAnalysis(db, seed.AssetId, seed.VersionId);

        var capacity = new AnalysisCapacityStore(
            db,
            MsOptions.Create(new CodeAnalysisOptions { Enabled = true, ServiceKey = SERVICE_KEY, Capacity = 1 }),
            NullLogger<AnalysisCapacityStore>.Instance);
        await capacity.EnsureSlots(SERVICE_KEY, 1);

        // A real analysis slot is occupied by a real reservation whose cleanup grace then elapses.
        ClaimedAssetProcessingJob occupied = (await capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "worker-a")).Single();
        AnalysisCapacitySlot slot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync();
        await db.AnalysisCapacitySlots
            .Where(s => s.Id == slot.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.CleanupGraceUntil, DateTimeOffset.UtcNow.AddHours(-1)));

        // The persisted approved row does not match the pinned manifest, so new claims stay closed.
        (await CreateGate(db, AdmittedReportTestEvidence.BuildChain().PolicyDigest, capacity: 1, withHandler: true)
            .EnsureReady()).Should().BeFalse();

        // Readiness spent nothing: the queued security job is untouched and no extra run appeared.
        AssetProcessingJob queued = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == seed.JobId);
        queued.Status.Should().Be(AssetProcessingJobStatus.QUEUED);
        queued.AttemptCount.Should().Be(0);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);

        // Recovery of occupied work still runs while new claims stay closed.
        var attemptsBeforeRecovery = (await db.AssetProcessingJobs.AsNoTracking()
            .SingleAsync(j => j.Id == occupied.JobId)).AttemptCount;

        (await capacity.RecoverExpiredReservations()).Should().Be(1);
        (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync()).State
            .Should().Be(AnalysisCapacitySlotState.FREE);

        // The reconciled reservation consumes exactly its own bounded attempt, nothing more.
        (await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == occupied.JobId)).AttemptCount
            .Should().Be(attemptsBeforeRecovery + 1);
    }

    /// <summary>A queued CODE_ANALYSIS run and its job, bound exactly as the enqueuer writes them.</summary>
    private static async Task SeedQueuedAnalysis(ApplicationDbContext db, Guid assetId, Guid versionId)
    {
        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == versionId);

        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));

        // Only the evidence the queued run references is needed here; the producing jobs are
        // irrelevant to readiness and would collide with this version's own security jobs.
        db.SecurityEvidenceRecords.Add(AdmittedReportTestEvidence.ArchiveEvidence(version, chain));
        db.SecurityEvidenceRecords.Add(AdmittedReportTestEvidence.MalwareEvidence(version, chain));

        var runId = Guid.NewGuid();
        var jobId = Guid.NewGuid();
        var inputDigest = AdmittedReportTestEvidence.NewHex64();

        db.CodeAnalysisRuns.Add(new CodeAnalysisRun
        {
            Id = runId,
            AssetId = assetId,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            PolicyVersionId = chain.PolicyVersionId,
            PolicyDigest = chain.PolicyDigest,
            DeclarationDigest = AdmittedReportTestEvidence.NewHex64(),
            MaterialDigest = AdmittedReportTestEvidence.NewHex64(),
            InputDigest = inputDigest,
            SelectionGeneration = 1,
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
            AssetId = assetId,
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
    }
    private sealed record SeedData(Guid JobId, Guid AssetId, Guid VersionId);

    private static async Task<SeedData> SeedVersionWithQueuedSecurityJob(ApplicationDbContext db)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Readiness subject");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var jobId = Guid.NewGuid();
        db.AssetProcessingJobs.Add(new AssetProcessingJob
        {
            Id = jobId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            Type = AssetProcessingJobType.MALWARE_SCAN,
            DefinitionVersion = 1,
            Status = AssetProcessingJobStatus.QUEUED,
            Stage = "QUEUED",
            AttemptCount = 0,
            MaxAttempts = 3,
            AvailableAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            CreatedAt = DateTimeOffset.UtcNow,
            Payload = AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.MALWARE_SCAN, new MalwareScanPayload("1.0"))
        });
        await db.SaveChangesAsync();
        return new SeedData(jobId, version.AssetId, version.Id);
    }

    private static AnalysisReadinessGate CreateGate(
        ApplicationDbContext db,
        string policyDigest,
        int capacity,
        bool withHandler,
        string policyKey = POLICY_KEY,
        string? runtimeManifestDigest = null) =>
        new(
            db,
            new AnalysisCapacityStore(
                db,
                MsOptions.Create(new CodeAnalysisOptions { Enabled = true, ServiceKey = SERVICE_KEY, Capacity = capacity }),
                NullLogger<AnalysisCapacityStore>.Instance),
            Substitute.For<ICodeAnalysisReportFinalizer>(),
            CreateRegistry(withHandler),
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = SERVICE_KEY,
                Capacity = capacity,
                PolicyKey = policyKey,
                PolicyDigest = policyDigest,
                RuntimeManifestDigest = runtimeManifestDigest ?? _pinnedManifestDigest
            }),
            NullLogger<AnalysisReadinessGate>.Instance);

    private static IAssetProcessingJobRegistry CreateRegistry(bool withHandler)
    {
        IAssetProcessingJobRegistry registry = Substitute.For<IAssetProcessingJobRegistry>();
        registry.GetHandler(AssetProcessingJobType.CODE_ANALYSIS)
            .Returns(withHandler ? Substitute.For<IAssetProcessingJobHandlerAdapter>() : null);
        return registry;
    }
}
