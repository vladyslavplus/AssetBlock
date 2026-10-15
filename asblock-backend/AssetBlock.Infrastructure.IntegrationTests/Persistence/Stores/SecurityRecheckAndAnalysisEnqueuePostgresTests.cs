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
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Focused PostgreSQL proof for the recheck and analysis enqueue writers: scoped conflict handling,
/// operation-receipt idempotency, generation selection, and the run/job binding that must survive a
/// losing race without leaving a half-bound pair behind.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class SecurityRecheckAndAnalysisEnqueuePostgresTests(PostgresFixture fixture)
{
    private const string POLICY_KEY = "code-analysis-policy-v1";

    [Fact]
    public async Task StartSecurityRecheck_WhenFirstRequest_ShouldCreateGenerationOneAsSelected()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);

        SecurityRecheckStartResult first = await Start(seed, operationReceipt: Guid.NewGuid());

        first.IsReplay.Should().BeFalse();
        first.SecurityGeneration.Should().Be(1);

        SecurityRecheck recheck = await db.SecurityRechecks.AsNoTracking().SingleAsync();
        recheck.State.Should().Be(SecurityRecheckState.QUEUED);
        recheck.SelectedGeneration.Should().Be(1);
        recheck.ContentSha256.Should().Be(seed.Version.ContentSha256);
    }

    [Fact]
    public async Task StartSecurityRecheck_WhenSameReceiptAndEnvelopeReplayed_ShouldReturnSameRecord()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        var receipt = Guid.NewGuid();

        SecurityRecheckStartResult first = await Start(seed, receipt);
        SecurityRecheckStartResult replay = await Start(seed, receipt);

        replay.IsReplay.Should().BeTrue();
        replay.RecheckId.Should().Be(first.RecheckId);
        replay.SecurityGeneration.Should().Be(first.SecurityGeneration);
        replay.ArchiveJobId.Should().Be(first.ArchiveJobId);

        (await db.SecurityRechecks.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task StartSecurityRecheck_WhenSameReceiptWithChangedEnvelope_ShouldConflictWithoutWrites()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        var receipt = Guid.NewGuid();

        SecurityRecheckStartResult first = await Start(seed, receipt);

        // Same stable operation receipt, different expectations: this must never create a generation.
        SecurityRecheckStartResult changed = await Start(seed, receipt, securityPolicyVersion: "policy-v2");

        changed.RecheckId.Should().Be(Guid.Empty);
        changed.IsReplay.Should().BeFalse();
        (await db.SecurityRechecks.AsNoTracking().CountAsync()).Should().Be(1);
        (await db.SecurityRechecks.AsNoTracking().SingleAsync()).Id.Should().Be(first.RecheckId);
    }

    [Fact]
    public async Task StartSecurityRecheck_WhenEquivalentWorkIsStillActive_ShouldDeduplicate()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);

        SecurityRecheckStartResult first = await Start(seed, Guid.NewGuid());
        SecurityRecheckStartResult second = await Start(seed, Guid.NewGuid());

        // Equivalent active work is deduplicated separately from a terminal retry.
        second.IsReplay.Should().BeTrue();
        second.RecheckId.Should().Be(first.RecheckId);
        (await db.SecurityRechecks.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task StartSecurityRecheck_WhenPriorGenerationIsTerminal_ShouldSwapSelectionWithoutUniqueViolation()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);

        SecurityRecheckStartResult first = await Start(seed, Guid.NewGuid());
        await db.SecurityRechecks
            .Where(r => r.Id == first.RecheckId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, SecurityRecheckState.ERROR)
                .SetProperty(r => r.ErrorCode, "SCANNER_UNAVAILABLE")
                .SetProperty(r => r.ErrorSummary, "scanner unavailable")
                .SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow));

        SecurityRecheckStartResult second = await Start(seed, Guid.NewGuid());

        second.SecurityGeneration.Should().Be(2);
        second.RecheckId.Should().NotBe(first.RecheckId);
        second.IsReplay.Should().BeFalse();

        List<SecurityRecheck> all = await db.SecurityRechecks.AsNoTracking()
            .OrderBy(r => r.SecurityGeneration)
            .ToListAsync();

        all.Should().HaveCount(2);

        // The older generation stays as history and is no longer selected.
        all[0].SelectedGeneration.Should().BeNull();
        all[1].SelectedGeneration.Should().Be(2);
    }

    [Fact]
    public async Task StartSecurityRecheck_WhenVersionHashDiffersFromPersisted_ShouldRefuse()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);

        SecurityRecheckStartResult result = await Start(seed, Guid.NewGuid(), contentSha256: new string('a', 64));

        result.RecheckId.Should().Be(Guid.Empty);
        (await db.SecurityRechecks.AsNoTracking().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task StartSecurityRecheck_WhenParentIsNotTerminalError_ShouldRefuseLinkedRetry()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);

        SecurityRecheckStartResult first = await Start(seed, Guid.NewGuid());

        // The parent is still QUEUED, so it is not an exhausted operational failure.
        SecurityRecheckStartResult retry = await Start(seed, Guid.NewGuid(), parentRecheckId: first.RecheckId);

        retry.RecheckId.Should().Be(Guid.Empty);
        (await db.SecurityRechecks.AsNoTracking().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task StartSecurityRecheck_WhenParentIsTerminalError_ShouldCreateLinkedGeneration()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);

        SecurityRecheckStartResult first = await Start(seed, Guid.NewGuid());
        await db.SecurityRechecks
            .Where(r => r.Id == first.RecheckId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.State, SecurityRecheckState.ERROR)
                .SetProperty(r => r.ErrorCode, "SCANNER_UNAVAILABLE")
                .SetProperty(r => r.ErrorSummary, "scanner unavailable")
                .SetProperty(r => r.CompletedAt, DateTimeOffset.UtcNow));

        SecurityRecheckStartResult retry = await Start(seed, Guid.NewGuid(), parentRecheckId: first.RecheckId);

        retry.IsReplay.Should().BeFalse();
        retry.SecurityGeneration.Should().Be(2);

        SecurityRecheck linked = await db.SecurityRechecks.AsNoTracking().SingleAsync(r => r.Id == retry.RecheckId);
        linked.ParentRecheckId.Should().Be(first.RecheckId);
        linked.SelectedGeneration.Should().Be(2);
    }

    [Fact]
    public async Task Enqueue_WhenCalledTwiceForSameInput_ShouldReturnSameRunAndJob()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);

        AnalysisRunEnqueueResult first = await seed.Enqueuer.Enqueue(
            seed.AssetId, seed.Version.Id, null,
            seed.Chain.ArchiveEvidenceId, seed.Chain.MalwareEvidenceId, 1,
            seed.Version.ContentSha256, DateTimeOffset.UtcNow);

        AnalysisRunEnqueueResult second = await seed.Enqueuer.Enqueue(
            seed.AssetId, seed.Version.Id, null,
            seed.Chain.ArchiveEvidenceId, seed.Chain.MalwareEvidenceId, 1,
            seed.Version.ContentSha256, DateTimeOffset.UtcNow);

        first.AnalysisRunId.Should().NotBe(Guid.Empty);
        first.IsReplay.Should().BeFalse();
        second.IsReplay.Should().BeTrue();
        second.AnalysisRunId.Should().Be(first.AnalysisRunId);
        second.AnalysisJobId.Should().Be(first.AnalysisJobId);

        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(1);
        (await db.AssetProcessingJobs.AsNoTracking().CountAsync(j => j.Type == AssetProcessingJobType.CODE_ANALYSIS))
            .Should().Be(1);
    }

    [Fact]
    public async Task Enqueue_WhenCreated_ShouldBindRunToJobAndJobToRun()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);

        AnalysisRunEnqueueResult result = await seed.Enqueuer.Enqueue(
            seed.AssetId, seed.Version.Id, null,
            seed.Chain.ArchiveEvidenceId, seed.Chain.MalwareEvidenceId, 1,
            seed.Version.ContentSha256, DateTimeOffset.UtcNow);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == result.AnalysisRunId);
        AssetProcessingJob job = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == result.AnalysisJobId);

        run.JobId.Should().Be(job.Id);
        job.AnalysisRunId.Should().Be(run.Id);
        run.State.Should().Be(CodeAnalysisRunState.QUEUED);
        run.ServiceKey.Should().Be(seed.ServiceKey);
        run.InputDigest.Should().HaveLength(64);
        run.TransportLeaseToken.Should().BeNull();

        // The compact job payload carries references only, never the report body.
        AssetProcessingPayload payload = AssetProcessingSerializer.DeserializePayload(
            AssetProcessingJobType.CODE_ANALYSIS, job.Payload!);
        var typed = (CodeAnalysisPayload)payload;
        typed.AnalysisRunId.Should().Be(run.Id);
        typed.InputDigest.Should().Be(run.InputDigest);
    }

    [Fact]
    public async Task Enqueue_WhenPinnedPolicyIsMissing_ShouldNotCreateRun()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedData seed = await Seed(db);
        await db.CodeAnalysisPolicyVersions.AsNoTracking().ExecuteDeleteAsync();

        AnalysisRunEnqueueResult result = await seed.Enqueuer.Enqueue(
            seed.AssetId, seed.Version.Id, null,
            seed.Chain.ArchiveEvidenceId, seed.Chain.MalwareEvidenceId, 1,
            seed.Version.ContentSha256, DateTimeOffset.UtcNow);

        // No admitted policy means no analysis to run, rather than an unpinned fallback.
        result.AnalysisRunId.Should().Be(Guid.Empty);
        (await db.CodeAnalysisRuns.AsNoTracking().CountAsync()).Should().Be(0);
    }

    private static Task<SecurityRecheckStartResult> Start(
        SeedData seed,
        Guid operationReceipt,
        string? securityPolicyVersion = null,
        string? contentSha256 = null,
        Guid? parentRecheckId = null)
    {
        var policy = securityPolicyVersion ?? "policy-v1";
        var sha = contentSha256 ?? seed.Version.ContentSha256;

        return seed.Provenance.StartSecurityRecheck(new SecurityRecheckStartRequest(
            seed.AssetId,
            seed.Version.Id,
            sha,
            policy,
            1,
            ProductDigests.BuildRecheckOperationDigest(seed.AssetId, seed.Version.Id, sha, policy, 1),
            operationReceipt,
            parentRecheckId));
    }

    private sealed record SeedData(
        ApplicationDbContext Db,
        Guid AssetId,
        AssetVersion Version,
        AdmittedReportTestEvidence.Chain Chain,
        ISecurityProvenanceStore Provenance,
        IAnalysisRunEnqueuer Enqueuer,
        string ServiceKey);

    private static async Task<SeedData> Seed(ApplicationDbContext db)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Recheck subject");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        await db.SaveChangesAsync();
        await AdmittedReportTestEvidence.SeedObservedProofAsync(db, version, chain);

        ISecurityProvenanceStore provenance = new SecurityProvenanceStore(
            db,
            MsOptions.Create(new AssetProcessingOptions()),
            new AnalysisRunEnqueuer(
                db,
                MsOptions.Create(new CodeAnalysisOptions
                {
                    Enabled = true,
                    ServiceKey = "code-analysis",
                    PolicyKey = POLICY_KEY,
                    PolicyDigest = chain.PolicyDigest
                }),
                MsOptions.Create(new AssetProcessingOptions()),
                NullLogger<AnalysisRunEnqueuer>.Instance),
            NullLogger<SecurityProvenanceStore>.Instance);

        IAnalysisRunEnqueuer enqueuer = new AnalysisRunEnqueuer(
            db,
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = "code-analysis",
                PolicyKey = POLICY_KEY,
                PolicyDigest = chain.PolicyDigest
            }),
            MsOptions.Create(new AssetProcessingOptions()),
            NullLogger<AnalysisRunEnqueuer>.Instance);

        return new SeedData(db, asset.Id, version, chain, provenance, enqueuer, "code-analysis");
    }
}
