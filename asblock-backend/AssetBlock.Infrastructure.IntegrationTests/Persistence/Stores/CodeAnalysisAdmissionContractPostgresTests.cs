using System.Text.Json.Nodes;
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
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Proves report admission is held to the real analysis-service contract and to the same current
/// security proof scheduling was. A completed envelope is admitted only when the exact run, input,
/// policy, security evidence, reservation and eight mandatory stages all bind, and only while the
/// security proof the run was created from is still current under the shared contract. An execution
/// failure envelope is recognized only when it is exactly the service's own FAILED or CANCELLED
/// envelope for this run; anything else is refused as an invalid response rather than being allowed
/// to reset or fail a live run.
/// </summary>
[Collection(nameof(PostgresStoreCollection))]
public sealed class CodeAnalysisAdmissionContractPostgresTests(PostgresFixture fixture)
{
    private const string SERVICE_KEY = "code-analysis";
    private const string POLICY_KEY = "code-analysis-policy-v1";

    [Fact]
    public async Task FinalizeConfirmed_WhenServiceReturnsRealCompletedEnvelope_ShouldAdmitWithBoundIdentity()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await Seed(db);
        ClaimedAssetProcessingJob claimed = await Claim(seed);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion policy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();
        var body = PythonAnalysisEnvelope.CompletedEnvelope(
            PythonAnalysisEnvelope.Bind(run),
            policy,
            declaredLicensesJson: "[]",
            declarationDigest: run.DeclarationDigest,
            materialDigest: run.MaterialDigest);

        AssetProcessingJobOutcome outcome = await Finalize(db, claimed, body);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);

        Guid reportId = await db.CodeAnalysisReportHeaders.AsNoTracking()
            .Where(h => h.AnalysisRunId == seed.RunId)
            .Select(h => h.Id)
            .SingleAsync();

        // The eight mandatory stages are persisted with the identity each real stage reports.
        List<CodeAnalysisReportStage> stages = await db.CodeAnalysisReportStages.AsNoTracking()
            .Where(s => s.ReportId == reportId)
            .OrderBy(s => s.Stage)
            .ToListAsync();
        stages.Should().HaveCount(8);
        stages.Select(s => s.Stage).Should().BeEquivalentTo(
            ["INVENTORY", "EXTRACTION", "TOKEN", "SEMANTIC", "LICENSE", "UNION", "VERIFIER", "REPORT"]);

        CodeAnalysisReportStage semantic = stages.Single(s => s.Stage == "SEMANTIC");
        semantic.ToolIdentityJson.Should().Contain(AdmittedReportTestEvidence.MODEL_KEY);
        semantic.ToolIdentityJson.Should().Contain("padding-masked mean then L2");
        stages.Single(s => s.Stage == "TOKEN").ToolIdentityJson.Should().Contain("dolos");
        stages.Single(s => s.Stage == "LICENSE").ToolIdentityJson.Should().Contain("scancode");
        stages.Single(s => s.Stage == "VERIFIER").ToolIdentityJson.Should().Contain("PENDING_RESEARCH_EVALUATION");

        // The baseline stays closed: the service carries no publication authority here.
        CodeAnalysisReportAdmission admission = await db.CodeAnalysisReportAdmissions.AsNoTracking()
            .SingleAsync(a => a.ReportId == reportId);
        admission.CanAuthorizePublication.Should().BeFalse();
        admission.AdmissionKind.Should().Be(CodeAnalysisAdmissionKind.SERVER_VALIDATED);
        admission.InputDigest.Should().Be(run.InputDigest);
        admission.ReservationGeneration.Should().Be(1);

        CodeAnalysisReportHeader header = await db.CodeAnalysisReportHeaders.AsNoTracking().SingleAsync();
        header.CanAuthorizePublication.Should().BeFalse();
        header.Disposition.Should().Be(CodeAnalysisReportDisposition.SELECTED);
    }

    [Theory]
    [InlineData("FAILED", "MODEL_UNAVAILABLE")]
    [InlineData("CANCELLED", "CANCELLED")]
    public async Task FinalizeConfirmed_WhenServiceReturnsRealFailureEnvelope_ShouldNeverAdmit(string outcomeText, string errorCode)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await Seed(db);
        ClaimedAssetProcessingJob claimed = await Claim(seed);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion failurePolicy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();
        var body = PythonAnalysisEnvelope.FailureEnvelope(
            PythonAnalysisEnvelope.Bind(run),
            new PythonAnalysisEnvelope.Options
            {
                Outcome = outcomeText,
                ErrorCode = errorCode,
                Retryable = false
            },
            outcomeText == "CANCELLED"
                ? PythonAnalysisEnvelope.FailureShape.CANCELLATION
                : PythonAnalysisEnvelope.FailureShape.STAGE_FAILURE,
            failurePolicy);

        AssetProcessingJobOutcome outcome = await Finalize(db, claimed, body);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        await AssertNoAdmission(db, seed);

        // A genuine operational failure keeps its own bounded service code on both the run and the
        // job; ANALYSIS_RESPONSE_INVALID is reserved for an envelope the server refused.
        CodeAnalysisRun failedRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        failedRun.State.Should().Be(CodeAnalysisRunState.FAILED);
        failedRun.ErrorCode.Should().Be(errorCode);

        AssetProcessingJob failedJob = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId);
        failedJob.Status.Should().Be(AssetProcessingJobStatus.FAILED);
        failedJob.ErrorCode.Should().Be(errorCode);

        // An operational failure is not an integrity problem: the version is never quarantined.
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SafetyRestriction
            .Should().Be(AssetVersionSafetyRestriction.NONE);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenRetryableFailureEnvelope_ShouldRescheduleWithoutAdmission()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await Seed(db);
        ClaimedAssetProcessingJob claimed = await Claim(seed);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion retryPolicy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();

        // A genuinely retryable service failure: the tokenizer stage failed with a retryable code,
        // and the envelope restates the approved policy it ran under.
        var body = PythonAnalysisEnvelope.FailureEnvelope(
            PythonAnalysisEnvelope.Bind(run),
            new PythonAnalysisEnvelope.Options
            {
                Outcome = "FAILED",
                ErrorCode = "TOKENIZER_UNAVAILABLE",
                FailedStage = "TOKEN",
                Retryable = true
            },
            PythonAnalysisEnvelope.FailureShape.STAGE_FAILURE,
            retryPolicy);

        AssetProcessingJobOutcome outcome = await Finalize(db, claimed, body);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.RETRY_SCHEDULED);
        await AssertNoAdmission(db, seed);
        (await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId)).State
            .Should().Be(CodeAnalysisRunState.QUEUED);
    }

    [Theory]
    [InlineData("runId")]
    [InlineData("inputDigest")]
    [InlineData("contentSha256")]
    [InlineData("assetId")]
    [InlineData("schema")]
    [InlineData("admittedBy")]
    [InlineData("findings")]
    [InlineData("stages")]
    public async Task FinalizeConfirmed_WhenFailureEnvelopeIsNotThisRunsOwnContract_ShouldBeRefusedNotApplied(string forgery)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await Seed(db);
        ClaimedAssetProcessingJob claimed = await Claim(seed);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = PythonAnalysisEnvelope.FailureEnvelope(
            PythonAnalysisEnvelope.Bind(run),
            Forgery(forgery));

        AssetProcessingJobOutcome outcome = await Finalize(db, claimed, body);

        // A foreign, altered or authority-bearing failure is an invalid response, never a reason to
        // reset or fail this run.
        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        await AssertNoAdmission(db, seed);
        CodeAnalysisRun failed = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        failed.State.Should().Be(CodeAnalysisRunState.FAILED);
        failed.ErrorCode.Should().Be(ErrorCodes.ANALYSIS_RESPONSE_INVALID);
    }

    private static PythonAnalysisEnvelope.Options Forgery(string forgery) =>
        forgery switch
        {
            "runId" => new PythonAnalysisEnvelope.Options { Outcome = "FAILED", RunId = Guid.NewGuid().ToString("D") },
            "inputDigest" => new PythonAnalysisEnvelope.Options { Outcome = "FAILED", InputDigest = AdmittedReportTestEvidence.NewHex64() },
            "contentSha256" => new PythonAnalysisEnvelope.Options { Outcome = "FAILED", ContentSha256 = AdmittedReportTestEvidence.NewHex64() },
            "assetId" => new PythonAnalysisEnvelope.Options { Outcome = "FAILED", AssetId = Guid.NewGuid().ToString("D") },
            "schema" => new PythonAnalysisEnvelope.Options { Outcome = "FAILED", Schema = "code-analysis-v9" },
            "admittedBy" => new PythonAnalysisEnvelope.Options { Outcome = "FAILED", AdmittedByFabricated = true },
            "findings" => new PythonAnalysisEnvelope.Options { Outcome = "FAILED", IncludeFailureFindings = true },
            _ => new PythonAnalysisEnvelope.Options { Outcome = "FAILED", IncludeFailureStages = true }
        };

    [Theory]
    [InlineData(ProofBreakage.NONE)]
    [InlineData(ProofBreakage.MISSING_PRODUCER)]
    [InlineData(ProofBreakage.PRODUCER_NEVER_SUCCEEDED)]
    [InlineData(ProofBreakage.ALTERED_POLICY)]
    public async Task FinalizeConfirmed_WhenTheBoundSecurityProofIsNotCurrent_ShouldRefuseAdmission(ProofBreakage breakage)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await Seed(db);
        await BreakProof(db, seed, breakage);
        ClaimedAssetProcessingJob claimed = await Claim(seed);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion policy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();
        var body = PythonAnalysisEnvelope.CompletedEnvelope(
            PythonAnalysisEnvelope.Bind(run),
            policy,
            declaredLicensesJson: "[]",
            declarationDigest: run.DeclarationDigest,
            materialDigest: run.MaterialDigest);

        AssetProcessingJobOutcome outcome = await Finalize(db, claimed, body);

        // A perfectly valid service response still cannot admit a report while the proof the run was
        // created from has stopped being current proof.
        if (breakage == ProofBreakage.NONE)
        {
            outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
                .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
            return;
        }

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        await AssertNoAdmission(db, seed);
        (await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId)).State
            .Should().Be(CodeAnalysisRunState.FAILED);
    }

    public enum ProofBreakage
    {
        NONE,
        MISSING_PRODUCER,
        PRODUCER_NEVER_SUCCEEDED,
        ALTERED_POLICY
    }

    private static async Task BreakProof(ApplicationDbContext db, SeedContext seed, ProofBreakage breakage)
    {
        switch (breakage)
        {
            case ProofBreakage.MISSING_PRODUCER:
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE security_evidence_records SET "JobId" = NULL WHERE "Id" = {seed.Chain.ArchiveEvidenceId}
                    """);
                break;
            case ProofBreakage.PRODUCER_NEVER_SUCCEEDED:
                // The archive proof's own producing job is the one that must stop being a succeeded
                // observation, so the proof can no longer name a successful producer.
                Guid producerJobId = await db.SecurityEvidenceRecords.AsNoTracking()
                    .Where(e => e.Id == seed.Chain.ArchiveEvidenceId)
                    .Select(e => e.JobId!.Value)
                    .SingleAsync();

                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE asset_processing_jobs
                    SET "Status" = 'FAILED', "Stage" = 'FAILED', "CompletedAt" = clock_timestamp()
                    WHERE "Id" = {producerJobId}
                    """);
                break;
            case ProofBreakage.ALTERED_POLICY:
                await db.Database.ExecuteSqlInterpolatedAsync($"""
                    UPDATE security_evidence_records
                    SET "SecurityPolicyVersion" = 'policy-v9-altered'
                    WHERE "Id" = {seed.Chain.MalwareEvidenceId}
                    """);
                break;
        }
    }

    [Theory]
    [InlineData("sellerSafeMissing")]
    [InlineData("sellerSafeAuthority")]
    [InlineData("sellerSafeForeignIdentity")]
    [InlineData("sellerSafeHidesFinding")]
    [InlineData("sellerSafeWrongReport")]
    [InlineData("stageInputDigestMissing")]
    [InlineData("stageToolIdentityMissing")]
    [InlineData("stageCountMissing")]
    [InlineData("unknownFailureOutcome")]
    public async Task FinalizeConfirmed_WhenTheServiceResponseIsNotItsOwnContract_ShouldRefuseAdmission(string forgery)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await Seed(db);
        ClaimedAssetProcessingJob claimed = await Claim(seed);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion policy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();

        var body = forgery == "unknownFailureOutcome"
            ? PythonAnalysisEnvelope.FailureEnvelope(
                PythonAnalysisEnvelope.Bind(run),
                new PythonAnalysisEnvelope.Options { Outcome = "UNKNOWN", ErrorCode = "INTERNAL_ERROR" })
            : PythonAnalysisEnvelope.CompletedEnvelope(
                PythonAnalysisEnvelope.Bind(run),
                policy,
                declaredLicensesJson: "[]",
                declarationDigest: run.DeclarationDigest,
                materialDigest: run.MaterialDigest,
                mutate: Mutation(forgery));

        AssetProcessingJobOutcome outcome = await Finalize(db, claimed, body);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();
        await AssertNoAdmission(db, seed);
        (await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId)).State
            .Should().Be(CodeAnalysisRunState.FAILED);
    }

    private static Func<JsonObject, JsonObject> Mutation(string forgery) =>
        forgery switch
        {
            "sellerSafeMissing" => body =>
            {
                body.Remove("sellerSafe");
                return body;
            }
            ,
            "sellerSafeAuthority" => body =>
            {
                ((JsonObject)body["sellerSafe"]!)["canAuthorizePublication"] = true;
                return body;
            }
            ,
            "sellerSafeForeignIdentity" => body =>
            {
                // Foreign identity, attribution detail and scores must never reach a seller consumer.
                var safe = (JsonArray)((JsonObject)body["sellerSafe"]!)["findings"]!;
                ((JsonObject)safe[0]!)["foreign"] = new JsonObject
                {
                    ["sourceId"] = "another-owners-private-asset",
                    ["path"] = "private/secret.cs",
                    ["sensitivity"] = "PRIVATE"
                };
                ((JsonObject)safe[0]!)["channelScore"] = 0.91;
                return body;
            }
            ,
            "sellerSafeHidesFinding" => body =>
            {
                ((JsonArray)((JsonObject)body["sellerSafe"]!)["findings"]!).RemoveAt(0);
                return body;
            }
            ,
            "sellerSafeWrongReport" => body =>
            {
                ((JsonObject)body["sellerSafe"]!)["reportId"] = AdmittedReportTestEvidence.NewHex64();
                return body;
            }
            ,
            "stageInputDigestMissing" => body =>
            {
                ((JsonObject)((JsonArray)body["stages"]!)[3]!).Remove("inputSha256");
                return body;
            }
            ,
            "stageToolIdentityMissing" => body =>
            {
                ((JsonObject)((JsonArray)body["stages"]!)[3]!).Remove("toolIdentity");
                return body;
            }
            ,
            _ => body =>
            {
                ((JsonObject)((JsonArray)body["stages"]!)[3]!).Remove("counts");
                return body;
            }
        };

    [Fact]
    public async Task FinalizeConfirmed_WhenTheJobLeaseIsNoLongerLive_ShouldRefuseAdmission()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await Seed(db);
        ClaimedAssetProcessingJob claimed = await Claim(seed);

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion policy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();
        var body = PythonAnalysisEnvelope.CompletedEnvelope(
            PythonAnalysisEnvelope.Bind(run),
            policy,
            declaredLicensesJson: "[]",
            declarationDigest: run.DeclarationDigest,
            materialDigest: run.MaterialDigest);

        // The lease expires while the response is in flight, so the delivery can no longer authorize
        // a write even though the response itself is exactly what the service sends.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "LeaseExpiresAt" = clock_timestamp() - interval '1 minute'
            WHERE "Id" = {claimed.JobId}
            """);

        AssetProcessingJobOutcome outcome = await Finalize(db, claimed, body);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>();
        await AssertNoAdmission(db, seed);
    }

    private static async Task AssertNoAdmission(ApplicationDbContext db, SeedContext seed)
    {
        (await db.CodeAnalysisReportHeaders.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.CodeAnalysisReportStages.AsNoTracking().CountAsync()).Should().Be(0);
        (await db.CodeAnalysisReportFindings.AsNoTracking().CountAsync()).Should().Be(0);
    }

    private static Task<AssetProcessingJobOutcome> Finalize(
        ApplicationDbContext db,
        ClaimedAssetProcessingJob claimed,
        string body) =>
        new CodeAnalysisReportFinalizer(
            db,
            new CodeAnalysisReportValidator(),
            NullLogger<CodeAnalysisReportFinalizer>.Instance)
            .FinalizeConfirmed(claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);

    private static async Task<ClaimedAssetProcessingJob> Claim(SeedContext seed)
    {
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        return (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();
    }

    private sealed record SeedContext(
        Asset Asset,
        AssetVersion Version,
        Guid RunId,
        AdmittedReportTestEvidence.Chain Chain,
        IAnalysisCapacityStore Capacity);

    private static async Task<SeedContext> Seed(ApplicationDbContext db)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Admission contract subject");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
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
            Payload = AssetProcessingSerializer.SerializePayload(
                AssetProcessingJobType.CODE_ANALYSIS,
                new CodeAnalysisPayload(
                    chain.RunId, chain.InputDigest, chain.PolicyDigest, version.ContentSha256,
                    chain.ArchiveEvidenceId, chain.MalwareEvidenceId, 1))
        });
        await db.SaveChangesAsync();

        await db.CodeAnalysisRuns
            .Where(r => r.Id == chain.RunId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, jobId));
        db.ChangeTracker.Clear();

        IAnalysisCapacityStore capacity = new AnalysisCapacityStore(
            db,
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = SERVICE_KEY,
                PolicyKey = POLICY_KEY,
                PolicyDigest = chain.PolicyDigest,
                Capacity = 1
            }),
            NullLogger<AnalysisCapacityStore>.Instance);

        return new SeedContext(asset, version, chain.RunId, chain, capacity);
    }
}
