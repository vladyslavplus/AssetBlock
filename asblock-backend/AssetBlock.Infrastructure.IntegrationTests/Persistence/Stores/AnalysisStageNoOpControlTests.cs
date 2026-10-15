using System.Text.Json;
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

[Collection(nameof(PostgresStoreCollection))]
public sealed class AnalysisStageNoOpControlTests(PostgresFixture fixture)
{
    private const string SERVICE_KEY = "code-analysis";
    private const string POLICY_KEY = "code-analysis-policy-v1";

    [Fact]
    public async Task NoOpHelper_WhenMutationIsIdentity_ShouldAdmitEightStages()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await Seed(db);
        ClaimedAssetProcessingJob claimed = await Claim(seed);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion policy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();

        var body = PythonAnalysisEnvelope.CompletedEnvelopeWithSelfConsistentForgery(
            PythonAnalysisEnvelope.Bind(run),
            policy,
            unchanged => unchanged,
            run.DeclarationDigest,
            run.MaterialDigest);

        using var document = JsonDocument.Parse(body);
        var stageNames = document.RootElement.GetProperty("stages").EnumerateArray()
            .Select(s => s.GetProperty("stage").GetString()).ToList();
        stageNames.Should().HaveCount(8);
        stageNames.Count(s => s == "REPORT").Should().Be(1);

        AssetProcessingJobOutcome outcome = await Finalize(db, claimed, body);
        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        (await db.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync()).Should().Be(1);
    }

    private static Task<AssetProcessingJobOutcome> Finalize(ApplicationDbContext db, ClaimedAssetProcessingJob c, string body) =>
        new CodeAnalysisReportFinalizer(db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance)
            .FinalizeConfirmed(c.JobId, c.LeaseToken, body, CancellationToken.None);

    private static async Task<ClaimedAssetProcessingJob> Claim(SeedContext seed)
    {
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        return (await seed.Capacity.ClaimWithCapacity(1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "capacity-worker")).Single();
    }

    private sealed record SeedContext(Asset Asset, AssetVersion Version, Guid RunId, AdmittedReportTestEvidence.Chain Chain, IAnalysisCapacityStore Capacity);

    private static async Task<SeedContext> Seed(ApplicationDbContext db)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Stage provenance subject");
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        await AdmittedReportTestEvidence.SeedObservedProofAsync(db, version, chain);
        CodeAnalysisRun run = new()
        {
            Id = chain.RunId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            PolicyVersionId = chain.PolicyVersionId,
            PolicyDigest = chain.PolicyDigest,
            DeclarationDigest = AdmittedReportTestEvidence.NewHex64(),
            MaterialDigest = AdmittedReportTestEvidence.NewHex64(),
            InputDigest = chain.InputDigest,
            SelectionGeneration = 1,
            ArchiveEvidenceId = chain.ArchiveEvidenceId,
            MalwareEvidenceId = chain.MalwareEvidenceId,
            SecurityGeneration = 1,
            ServiceKey = SERVICE_KEY,
            State = CodeAnalysisRunState.QUEUED,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.CodeAnalysisRuns.Add(run);
        await db.SaveChangesAsync();
        var jobId = Guid.NewGuid();
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
            AnalysisRunId = chain.RunId,
            Payload = AssetProcessingSerializer.SerializePayload(AssetProcessingJobType.CODE_ANALYSIS, new CodeAnalysisPayload(chain.RunId, chain.InputDigest, chain.PolicyDigest, version.ContentSha256, chain.ArchiveEvidenceId, chain.MalwareEvidenceId, 1))
        });
        await db.SaveChangesAsync();
        await db.CodeAnalysisRuns.Where(r => r.Id == chain.RunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, jobId));
        db.ChangeTracker.Clear();
        IAnalysisCapacityStore capacity = new AnalysisCapacityStore(db, MsOptions.Create(new CodeAnalysisOptions { Enabled = true, ServiceKey = SERVICE_KEY, PolicyKey = POLICY_KEY, PolicyDigest = chain.PolicyDigest, Capacity = 1 }), NullLogger<AnalysisCapacityStore>.Instance);
        return new SeedContext(asset, version, chain.RunId, chain, capacity);
    }
}
