using System.Text.Json;
using System.Text.Json.Nodes;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class CodeAnalysisReportFinalizerPostgresTests(PostgresFixture fixture)
{
    private const string SERVICE_KEY = "code-analysis";
    private const string POLICY_KEY = "code-analysis-policy-v1";

    [Fact]
    public async Task FinalizeConfirmed_WhenValidCompletedReport_ShouldCommitHeaderStagesFindingsAdmissionAuditOutboxAndSelectVersion()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(db);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();

        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.PolicyDigest);

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            db,
            new CodeAnalysisReportValidator(),
            NullLogger<CodeAnalysisReportFinalizer>.Instance);

        AssetProcessingJobOutcome outcome = await finalizer.FinalizeConfirmed(
            claimed.JobId,
            claimed.LeaseToken,
            body,
            CancellationToken.None);

        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);

        Guid reportId = await db.CodeAnalysisReportHeaders.AsNoTracking()
            .Where(h => h.AnalysisRunId == seed.RunId)
            .Select(h => h.Id)
            .SingleAsync();

        CodeAnalysisReportHeader header = await db.CodeAnalysisReportHeaders.AsNoTracking().SingleAsync(h => h.Id == reportId);
        header.Disposition.Should().Be(CodeAnalysisReportDisposition.SELECTED);
        header.IsFinalized.Should().BeTrue();
        header.Purpose.Should().Be(CodeAnalysisReportPurpose.PRODUCTION);
        header.CanAuthorizePublication.Should().BeFalse();
        header.AnalyzedFileCount.Should().Be(1);
        header.OmissionCount.Should().Be(0);
        header.FindingCount.Should().Be(1);
        header.PolicyVersion.Should().Be(POLICY_KEY);
        header.AssetVersionId.Should().Be(seed.Version.Id);
        header.AssetId.Should().Be(seed.Asset.Id);

        (await db.CodeAnalysisReportStages.AsNoTracking().Where(s => s.ReportId == reportId).CountAsync()).Should().Be(8);
        (await db.CodeAnalysisReportFindings.AsNoTracking().Where(f => f.ReportId == reportId).CountAsync()).Should().Be(1);

        CodeAnalysisReportAdmission admission = await db.CodeAnalysisReportAdmissions.AsNoTracking().SingleAsync(a => a.ReportId == reportId);
        admission.AdmissionKind.Should().Be(CodeAnalysisAdmissionKind.SERVER_VALIDATED);
        admission.AdmittedBy.Should().Be("assetblock-dotnet-finalizer");
        admission.CanAuthorizePublication.Should().BeFalse();
        admission.ReportDigest.Should().HaveLength(64);
        admission.InputDigest.Should().Be(run.InputDigest);
        admission.ServiceKey.Should().Be(SERVICE_KEY);
        admission.ReservationGeneration.Should().Be(1);
        admission.ReservationSlotNumber.Should().Be(
            (await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync(s => s.ReservedRunId == seed.RunId)).SlotNumber);
        admission.PythonReportId.Should().Be(header.PythonReportId);

        CodeAnalysisRun completedRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        completedRun.State.Should().Be(CodeAnalysisRunState.COMPLETED);
        completedRun.Disposition.Should().Be(CodeAnalysisReportDisposition.SELECTED);

        AssetProcessingJob job = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId);
        job.Status.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        job.LeaseToken.Should().BeNull();
        job.TransportLeaseToken.Should().BeNull();
        job.LeaseExpiresAt.Should().BeNull();

        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id);
        version.SelectedCodeAnalysisReportId.Should().Be(reportId);
        version.IsCurrent.Should().BeTrue();
        version.SafetyRestriction.Should().Be(AssetVersionSafetyRestriction.NONE);

        (await db.AuditLogs.AsNoTracking().Where(a => a.Action == "CODE_ANALYSIS_FINALIZED").CountAsync()).Should().Be(1);
        OutboxMessage outbox = await db.OutboxMessages.AsNoTracking().SingleAsync();
        outbox.Type.Should().Be(OutboxMessageTypes.NOTIFICATION_DISPATCH);
        JsonSerializerOptions outboxJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        NotificationDispatchPayload parsedDispatch = JsonSerializer.Deserialize<NotificationDispatchPayload>(outbox.Payload, outboxJson)!;
        parsedDispatch.RecipientUserId.Should().Be(seed.Asset.AuthorId);
        parsedDispatch.Kind.Should().Be(NotificationKind.CODE_ANALYSIS_COMPLETED);
        parsedDispatch.HubMethod.Should().Be(NotificationHubMethods.CODE_ANALYSIS_COMPLETED);
        parsedDispatch.MetadataJson.Should().NotBeNullOrWhiteSpace();
        using var parsedMetadata = JsonDocument.Parse(parsedDispatch.MetadataJson);
        using var expectedMetadata = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            jobId = claimed.JobId,
            assetId = seed.Asset.Id,
            assetVersionId = seed.Version.Id
        }));
        JsonNode.DeepEquals(JsonNode.Parse(parsedDispatch.MetadataJson), JsonNode.Parse(expectedMetadata.RootElement.GetRawText())).Should().BeTrue();
        (await db.UserNotifications.AsNoTracking().CountAsync(n => n.RecipientUserId == seed.Asset.AuthorId && n.Kind == NotificationKind.CODE_ANALYSIS_COMPLETED)).Should().Be(1);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenPythonReportsScopedAndMatchedLicenseFindings_ShouldPersistHonestLocations()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "scope-finding-worker")).Single();
        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion policy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();

        const string scopeReason = "Declared license scope has no scanned files: docs";
        const string matchedReason = "Declared MIT is not among detected signals under scope src.";
        var fileId = new string('e', 64);
        JsonObject scopeNotice = new()
        {
            ["findingId"] = PythonFindingId("NOTICE_MISSING", "docs", null, scopeReason),
            ["type"] = "NOTICE_MISSING",
            ["path"] = "docs",
            ["fileId"] = null,
            ["startLine"] = null,
            ["endLine"] = null,
            ["startByte"] = null,
            ["endByte"] = null,
            ["ownExcerpt"] = "",
            ["foreign"] = null,
            ["reason"] = scopeReason,
            ["requestedAction"] = "Add the required license notice for this declared scope."
        };
        JsonObject matchedLicense = new()
        {
            ["findingId"] = PythonFindingId("LICENSE_CONTRADICTION", "src/main.cs", fileId, matchedReason),
            ["type"] = "LICENSE_CONTRADICTION",
            ["path"] = "src/main.cs",
            ["fileId"] = fileId,
            ["startLine"] = 2,
            ["endLine"] = 3,
            ["startByte"] = 14,
            ["endByte"] = 47,
            ["ownExcerpt"] = "// SPDX-License-Identifier: Apache-2.0",
            ["foreign"] = null,
            ["reason"] = matchedReason,
            ["requestedAction"] = "Review the detected license and correct the declaration."
        };
        JsonObject root = JsonNode.Parse(BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.PolicyDigest))!.AsObject();
        root["findings"] = new JsonArray(scopeNotice, matchedLicense);
        root["stages"]!.AsArray().Select(node => node!.AsObject())
            .Single(stage => stage["stage"]!.GetValue<string>() == "UNION")["counts"]!["unionFindings"] = 2;
        var body = PythonAnalysisEnvelope.RegenerateDerivedIdentity(root).ToJsonString();

        var validator = new CodeAnalysisReportValidator();
        ValidatedCodeAnalysisReport projected = validator.ValidateCompleted(body, run, policy);
        projected.Findings.Single(x => x.FindingType == "NOTICE_MISSING").OwnScopePath.Should().Be("docs");
        projected.Findings.Single(x => x.FindingType == "NOTICE_MISSING").OwnStartByte.Should().BeNull();
        projected.Findings.Single(x => x.FindingType == "LICENSE_CONTRADICTION").OwnScopePath.Should().Be("src/main.cs");
        projected.Findings.Single(x => x.FindingType == "LICENSE_CONTRADICTION").OwnStartByte.Should().Be(14);
        projected.Findings.Single(x => x.FindingType == "LICENSE_CONTRADICTION").OwnStartLine.Should().Be(2);

        JsonObject fabricated = JsonNode.Parse(body)!.AsObject();
        fabricated["findings"]![0]!["fileId"] = fileId;
        var fabricatedBody = PythonAnalysisEnvelope.RegenerateDerivedIdentity(fabricated).ToJsonString();
        Action rejectFileBinding = () => validator.ValidateCompleted(fabricatedBody, run, policy);
        rejectFileBinding.Should().Throw<CodeAnalysisValidationException>();

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            db, validator, NullLogger<CodeAnalysisReportFinalizer>.Instance);
        AssetProcessingJobOutcome outcome = await finalizer.FinalizeConfirmed(
            claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);

        Guid reportId = await db.CodeAnalysisReportHeaders.AsNoTracking()
            .Where(header => header.AnalysisRunId == run.Id).Select(header => header.Id).SingleAsync();
        Dictionary<string, CodeAnalysisReportFinding> stored = await db.CodeAnalysisReportFindings.AsNoTracking()
            .Where(finding => finding.ReportId == reportId).ToDictionaryAsync(finding => finding.FindingType);
        stored["NOTICE_MISSING"].OwnScopePath.Should().Be("docs");
        stored["NOTICE_MISSING"].OwnStartByte.Should().BeNull();
        stored["NOTICE_MISSING"].OwnStartLine.Should().BeNull();
        stored["LICENSE_CONTRADICTION"].OwnScopePath.Should().Be("src/main.cs");
        stored["LICENSE_CONTRADICTION"].OwnStartByte.Should().Be(14);
        stored["LICENSE_CONTRADICTION"].OwnStartLine.Should().Be(2);
    }

    private static string PythonFindingId(string type, string path, string? fileId, string reason)
    {
        using var identity = JsonDocument.Parse(JsonSerializer.Serialize(
            new object?[] { type, path, fileId ?? "", "", reason }));
        return CanonicalJson.Sha256Hex(identity.RootElement);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenCompletedBodyCarriesReusableArtifacts_ShouldPersistSourceBoundRows()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(db);
        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "capacity-worker")).Single();
        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        CodeAnalysisPolicyVersion policy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleAsync();
        JsonObject root = JsonNode.Parse(BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.PolicyDigest))!.AsObject();
        using var manifest = JsonDocument.Parse(policy.ToolIdentitiesJson);
        var revision = manifest.RootElement.GetProperty("referenceRevision").GetString()!;
        (string Stage, string Data)[] cacheData =
        [
            ("EXTRACTION", "{\"analyzed\":[],\"omissions\":[],\"omissionOverflow\":0,\"coverageByDialect\":{},\"toolIdentity\":{}}"),
            ("TOKEN", "{\"ranked\":{},\"counts\":{},\"toolIdentity\":{}}"),
            ("SEMANTIC", "{\"windows\":[],\"vectors\":[],\"counts\":{},\"toolIdentity\":{}}")
        ];
        JsonArray artifactsJson = new();
        for (var index = 0; index < cacheData.Length; index++)
        {
            var stageName = cacheData[index].Stage;
            var dataJson = cacheData[index].Data;
            using var data = JsonDocument.Parse(dataJson);
            var artifactValue = new
            {
                binding = new
                {
                    sourceRunId = run.Id.ToString("D"),
                    sourceInputDigest = run.InputDigest,
                    contentSha256 = run.ContentSha256,
                    policyDigest = run.PolicyDigest,
                    materialDigest = run.MaterialDigest,
                    declarationDigest = run.DeclarationDigest,
                    securityGeneration = run.SecurityGeneration,
                    archiveEvidenceId = run.ArchiveEvidenceId.ToString("D"),
                    malwareEvidenceId = run.MalwareEvidenceId.ToString("D"),
                    stage = stageName,
                    snapshotId = AdmittedReportTestEvidence.REFERENCE_SNAPSHOT_ID,
                    snapshotRevision = revision,
                    modelKey = seed.Chain.ModelKey,
                    indexKey = seed.Chain.IndexKey,
                    modelIdentitySha256 = CodeAnalysisReportValidator.ReadModelIdentityDigest(policy)
                },
                data = data.RootElement
            };
            using var artifact = JsonDocument.Parse(JsonSerializer.Serialize(artifactValue));
            var canonical = CanonicalJson.CanonicalizeToString(artifact.RootElement);
            var digest = CanonicalJson.Sha256Hex(artifact.RootElement);
            artifactsJson.Add(new JsonObject
            {
                ["stage"] = stageName,
                ["artifactDigest"] = digest,
                ["artifactJson"] = canonical.TrimEnd('\n')
            });
            JsonObject stage = root["stages"]!.AsArray()
                .Select(node => node!.AsObject())
                .Single(node => node["stage"]!.GetValue<string>() == stageName);
            stage["outputSha256"] = digest;
        }

        root["reuseArtifacts"] = artifactsJson;
        var body = PythonAnalysisEnvelope.RegenerateDerivedIdentity(root).ToJsonString();
        var validator = new CodeAnalysisReportValidator();
        _ = validator.ValidateCompleted(body, run, policy);

        var parentRunId = Guid.NewGuid();
        var parentReportId = Guid.NewGuid();
        var parentInputDigest = new string('a', 64);
        var parentDeclarationDigest = new string('b', 64);
        var parentArtifacts = new List<CodeAnalysisReuseStageArtifact>();
        JsonObject parentBound = JsonNode.Parse(body)!.AsObject();
        foreach (JsonObject artifactItem in parentBound["reuseArtifacts"]!.AsArray().Select(node => node!.AsObject()))
        {
            var stageName = artifactItem["stage"]!.GetValue<string>();
            var parentStageId = Guid.NewGuid();
            var parentArtifactDigest = new string((char)('c' + parentArtifacts.Count), 64);
            JsonObject stage = parentBound["stages"]!.AsArray().Select(node => node!.AsObject())
                .Single(node => node["stage"]!.GetValue<string>() == stageName);
            JsonObject tool = stage["toolIdentity"]!.AsObject();
            tool["reused"] = true;
            tool["reuseParentRunId"] = parentRunId.ToString("D");
            tool["reuseParentReportId"] = parentReportId.ToString("D");
            tool["reuseParentStageId"] = parentStageId.ToString("D");
            tool["reuseArtifactDigest"] = parentArtifactDigest;
            parentArtifacts.Add(new CodeAnalysisReuseStageArtifact(
                stageName, parentReportId.ToString("D"), parentStageId.ToString("D"), parentArtifactDigest,
                artifactItem["artifactJson"]!.GetValue<string>()));
        }
        var parentBoundBody = PythonAnalysisEnvelope.RegenerateDerivedIdentity(parentBound).ToJsonString();
        _ = validator.ValidateCompleted(parentBoundBody, run, policy, new CodeAnalysisReuseSourceBinding(
            parentRunId.ToString("D"), parentInputDigest, parentDeclarationDigest, run.MaterialDigest, parentArtifacts));

        JsonObject foreignStage = JsonNode.Parse(parentBoundBody)!.AsObject();
        JsonObject extractionStage = foreignStage["stages"]!.AsArray().Select(node => node!.AsObject())
            .Single(node => node["stage"]!.GetValue<string>() == "EXTRACTION");
        extractionStage["toolIdentity"]!["reuseParentStageId"] = Guid.NewGuid().ToString("D");
        var foreignStageBody = PythonAnalysisEnvelope.RegenerateDerivedIdentity(foreignStage).ToJsonString();
        Action foreignStageBinding = () => validator.ValidateCompleted(foreignStageBody, run, policy,
            new CodeAnalysisReuseSourceBinding(parentRunId.ToString("D"), parentInputDigest,
                parentDeclarationDigest, run.MaterialDigest, parentArtifacts));
        foreignStageBinding.Should().Throw<CodeAnalysisValidationException>();

        JsonObject forged = JsonNode.Parse(body)!.AsObject();
        foreach (JsonObject artifactItem in forged["reuseArtifacts"]!.AsArray().Select(node => node!.AsObject()))
        {
            var stageName = artifactItem["stage"]!.GetValue<string>();
            using var artifactDocument = JsonDocument.Parse(artifactItem["artifactJson"]!.GetValue<string>());
            JsonObject artifact = JsonNode.Parse(artifactDocument.RootElement.GetRawText())!.AsObject();
            artifact["binding"]!["modelIdentitySha256"] = new string('f', 64);
            using var changedArtifact = JsonDocument.Parse(artifact.ToJsonString());
            var canonical = CanonicalJson.CanonicalizeToString(changedArtifact.RootElement);
            artifactItem["artifactJson"] = canonical.TrimEnd('\n');
            artifactItem["artifactDigest"] = CanonicalJson.Sha256Hex(changedArtifact.RootElement);
            JsonObject stage = forged["stages"]!.AsArray().Select(node => node!.AsObject())
                .Single(node => node["stage"]!.GetValue<string>() == stageName);
            stage["outputSha256"] = artifactItem["artifactDigest"]!.GetValue<string>();
        }
        var forgedBody = PythonAnalysisEnvelope.RegenerateDerivedIdentity(forged).ToJsonString();
        Action forgedIdentity = () => validator.ValidateCompleted(forgedBody, run, policy);
        forgedIdentity.Should().Throw<CodeAnalysisValidationException>();

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        AssetProcessingJobOutcome outcome = await finalizer.FinalizeConfirmed(
            claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        AssetProcessingJob failedJob = await db.AssetProcessingJobs.AsNoTracking().SingleAsync(job => job.Id == claimed.JobId);
        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED, "finalizer error was {0}", failedJob.ErrorCode);

        Guid reportId = await db.CodeAnalysisReportHeaders.AsNoTracking()
            .Where(header => header.AnalysisRunId == run.Id).Select(header => header.Id).SingleAsync();
        List<CodeAnalysisReuseArtifact> persisted = await db.CodeAnalysisReuseArtifacts.AsNoTracking()
            .Where(artifact => artifact.SourceReportId == reportId && artifact.SourceRunId == run.Id)
            .OrderBy(artifact => artifact.Stage).ToListAsync();
        persisted.Select(artifact => artifact.Stage).Should().Equal("EXTRACTION", "SEMANTIC", "TOKEN");
        foreach (CodeAnalysisReuseArtifact artifact in persisted)
        {
            CodeAnalysisReportStage stage = await db.CodeAnalysisReportStages.AsNoTracking()
                .SingleAsync(candidate => candidate.Id == artifact.SourceStageId);
            stage.OutputSha256.Should().Be(artifact.ArtifactDigest);
            artifact.PayloadBytes.Should().BeGreaterThan(0);
            using var stored = JsonDocument.Parse(artifact.ArtifactJson);
            stored.RootElement.GetProperty("binding").GetProperty("sourceRunId").GetString()
                .Should().Be(run.Id.ToString("D"));
        }
    }

    /// <summary>
    /// The completed envelope the analysis service actually sends for this run, rebuilt on the real
    /// contract fixture: exact bindings, the eight mandatory stages with their real per-stage tool
    /// identity and counts, the recomputed report identity, and the mandatory owner-safe projection.
    /// </summary>
    internal static string BuildResponseBody(
        CodeAnalysisRun run,
        AssetVersion version,
        Asset asset,
        AdmittedReportTestEvidence.Chain chain,
        string policyDigest,
        string findingReasonOverride = "Representative finding.")
    {
        CodeAnalysisPolicyVersion policy = new()
        {
            Id = chain.PolicyVersionId,
            PolicyKey = POLICY_KEY,
            PolicyDigest = policyDigest,
            SchemaName = "code-analysis-v1",
            StageMatrixJson = "{}",
            VerifierMode = "PENDING_RESEARCH_EVALUATION",
            VerifierReason = "Retrieval quality research evaluation has not been reviewed.",
            ToolIdentitiesJson = "{}",
            LimitsJson = "{}",
            SecurityValidityJson = "{}",
            ReferenceSnapshotId = AdmittedReportTestEvidence.REFERENCE_SNAPSHOT_ID,
            CreatedAt = DateTimeOffset.UtcNow
        };

        return PythonAnalysisEnvelope.CompletedEnvelope(
            PythonAnalysisEnvelope.Bind(run),
            policy,
            new PythonAnalysisEnvelope.Options { FindingReason = findingReasonOverride },
            declaredLicensesJson: "[]",
            declarationDigest: run.DeclarationDigest,
            materialDigest: run.MaterialDigest);
    }
    [Fact]
    public async Task FinalizeConfirmed_WhenSameResponseIsReplayed_ShouldReturnFrozenReportWithoutDuplicateEffects()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(db);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();
        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.PolicyDigest);

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);

        AssetProcessingJobOutcome first = await finalizer.FinalizeConfirmed(
            claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);
        first.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();

        Guid selectedBefore = (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SelectedCodeAnalysisReportId!.Value;
        var headersBefore = await db.CodeAnalysisReportHeaders.AsNoTracking().CountAsync();
        var stagesBefore = await db.CodeAnalysisReportStages.AsNoTracking().CountAsync();
        var findingsBefore = await db.CodeAnalysisReportFindings.AsNoTracking().CountAsync();
        var admissionsBefore = await db.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync();
        var auditsBefore = await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "CODE_ANALYSIS_FINALIZED");
        var outboxesBefore = await db.OutboxMessages.AsNoTracking().CountAsync();

        AssetProcessingJobOutcome replay = await finalizer.FinalizeConfirmed(
            claimed.JobId, Guid.NewGuid(), body, CancellationToken.None);
        replay.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);

        (await db.CodeAnalysisReportHeaders.AsNoTracking().CountAsync()).Should().Be(headersBefore);
        (await db.CodeAnalysisReportStages.AsNoTracking().CountAsync()).Should().Be(stagesBefore);
        (await db.CodeAnalysisReportFindings.AsNoTracking().CountAsync()).Should().Be(findingsBefore);
        (await db.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync()).Should().Be(admissionsBefore);
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "CODE_ANALYSIS_FINALIZED")).Should().Be(auditsBefore);
        (await db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(outboxesBefore);
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SelectedCodeAnalysisReportId!.Value
            .Should().Be(selectedBefore);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenFrozenReportReplayIsAltered_ShouldConflictWithoutNewEffects()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(db);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();
        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.PolicyDigest);

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);

        await finalizer.FinalizeConfirmed(claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);

        var headersBefore = await db.CodeAnalysisReportHeaders.AsNoTracking().CountAsync();
        var stagesBefore = await db.CodeAnalysisReportStages.AsNoTracking().CountAsync();
        var findingsBefore = await db.CodeAnalysisReportFindings.AsNoTracking().CountAsync();
        var admissionsBefore = await db.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync();
        var auditsBefore = await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "CODE_ANALYSIS_FINALIZED");
        var outboxesBefore = await db.OutboxMessages.AsNoTracking().CountAsync();
        var notificationsBefore = await db.UserNotifications.AsNoTracking().CountAsync();
        Guid? selectedBefore = (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SelectedCodeAnalysisReportId;

        // Fully self-consistent altered report for the same run: new reason is re-canonicalized
        // into a new pre-REPORT id, REPORT input/output binding, and sellerSafe id.
        var alteredBody = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.PolicyDigest, "Representative changed.");

        AssetProcessingJobOutcome outcome = await finalizer.FinalizeConfirmed(
            claimed.JobId, Guid.NewGuid(), alteredBody, CancellationToken.None);
        outcome.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>();

        (await db.CodeAnalysisReportHeaders.AsNoTracking().CountAsync()).Should().Be(headersBefore);
        (await db.CodeAnalysisReportStages.AsNoTracking().CountAsync()).Should().Be(stagesBefore);
        (await db.CodeAnalysisReportFindings.AsNoTracking().CountAsync()).Should().Be(findingsBefore);
        (await db.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync()).Should().Be(admissionsBefore);
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "CODE_ANALYSIS_FINALIZED")).Should().Be(auditsBefore);
        (await db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(outboxesBefore);
        (await db.UserNotifications.AsNoTracking().CountAsync()).Should().Be(notificationsBefore);
        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SelectedCodeAnalysisReportId.Should().Be(selectedBefore);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenReplayIsMalformedOrFailed_ShouldHaveNoEffectsAndOriginalReplayStillResolves()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(db);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();
        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.PolicyDigest);

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);

        await finalizer.FinalizeConfirmed(claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);

        var headersBefore = await db.CodeAnalysisReportHeaders.AsNoTracking().CountAsync();
        var stagesBefore = await db.CodeAnalysisReportStages.AsNoTracking().CountAsync();
        var findingsBefore = await db.CodeAnalysisReportFindings.AsNoTracking().CountAsync();
        var admissionsBefore = await db.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync();
        var auditsBefore = await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "CODE_ANALYSIS_FINALIZED");
        var outboxesBefore = await db.OutboxMessages.AsNoTracking().CountAsync();
        var notificationsBefore = await db.UserNotifications.AsNoTracking().CountAsync();

        AssetProcessingJobOutcome malformed = await finalizer.FinalizeConfirmed(
            claimed.JobId, Guid.NewGuid(), "not-json", CancellationToken.None);
        malformed.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>();

        AssetProcessingJobOutcome failedReplay = await finalizer.FinalizeConfirmed(
            claimed.JobId, Guid.NewGuid(), """{"outcome":"FAILED","errorCode":"ANALYSIS_CLIENT_ERROR","retryable":true}""", CancellationToken.None);
        failedReplay.Should().BeOfType<AssetProcessingJobOutcome.TerminalFailure>();

        (await db.CodeAnalysisReportHeaders.AsNoTracking().CountAsync()).Should().Be(headersBefore);
        (await db.CodeAnalysisReportStages.AsNoTracking().CountAsync()).Should().Be(stagesBefore);
        (await db.CodeAnalysisReportFindings.AsNoTracking().CountAsync()).Should().Be(findingsBefore);
        (await db.CodeAnalysisReportAdmissions.AsNoTracking().CountAsync()).Should().Be(admissionsBefore);
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "CODE_ANALYSIS_FINALIZED")).Should().Be(auditsBefore);
        (await db.OutboxMessages.AsNoTracking().CountAsync()).Should().Be(outboxesBefore);
        (await db.UserNotifications.AsNoTracking().CountAsync()).Should().Be(notificationsBefore);

        AssetProcessingJobOutcome replay = await finalizer.FinalizeConfirmed(
            claimed.JobId, Guid.NewGuid(), body, CancellationToken.None);
        replay.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
    }

    [Fact]
    public async Task FinalizeConfirmed_WhenAssetIsDeletedAfterFrozenReport_ShouldReplayWithoutSelectionMutation()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(db);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();
        CodeAnalysisRun run = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId);
        var body = BuildResponseBody(run, seed.Version, seed.Asset, seed.Chain, seed.PolicyDigest);

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);

        await finalizer.FinalizeConfirmed(claimed.JobId, claimed.LeaseToken, body, CancellationToken.None);

        await db.Assets.Where(a => a.Id == seed.Asset.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.DeletedAt, DateTimeOffset.UtcNow));
        Guid selectedBefore = (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SelectedCodeAnalysisReportId!.Value;

        AssetProcessingJobOutcome replay = await finalizer.FinalizeConfirmed(
            claimed.JobId, Guid.NewGuid(), body, CancellationToken.None);
        replay.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();

        (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id)).SelectedCodeAnalysisReportId!.Value
            .Should().Be(selectedBefore);
    }

    [Fact]
    public async Task QuarantineIntegrity_WhenLeaseIsLive_ShouldQuarantineVersionRunAndJob()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        SeedContext seed = await SeedQueued(db);

        await seed.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Capacity.ClaimWithCapacity(
            batchSize: 1,
            leaseDuration: TimeSpan.FromMinutes(5),
            hardDeadline: TimeSpan.FromMinutes(18),
            cleanupGrace: TimeSpan.FromMinutes(2),
            leaseOwner: "capacity-worker")).Single();

        ICodeAnalysisReportFinalizer finalizer = new CodeAnalysisReportFinalizer(
            db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);

        AssetProcessingJobOutcome outcome = await finalizer.QuarantineIntegrity(
            claimed.JobId, claimed.LeaseToken, "ARCHIVE_CORRUPT", CancellationToken.None);
        outcome.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.FAILED);

        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Version.Id);
        version.SafetyRestriction.Should().Be(AssetVersionSafetyRestriction.INTEGRITY_MISMATCH);
        version.SafetyRestrictionReason.Should().Be("ARCHIVE_CORRUPT");

        (await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == seed.RunId)).State
            .Should().Be(CodeAnalysisRunState.FAILED);
        (await db.AssetProcessingJobs.AsNoTracking().SingleAsync(j => j.Id == claimed.JobId)).Status
            .Should().Be(AssetProcessingJobStatus.FAILED);
        (await db.AuditLogs.AsNoTracking().Where(a => a.Action == "CODE_ANALYSIS_INTEGRITY_QUARANTINED").CountAsync())
            .Should().Be(1);
    }

    private sealed record ChainUnused;

    private sealed record SeedContext(
        Asset Asset,
        AssetVersion Version,
        Guid RunId,
        AdmittedReportTestEvidence.Chain Chain,
        IAnalysisCapacityStore Capacity,
        string PolicyDigest);

    private static async Task<SeedContext> SeedQueued(ApplicationDbContext db)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Finalizer subject");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        AdmittedReportTestEvidence.SeedObservedProof(db, version, chain);
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
            DeclarationDigest = AdmittedReportTestEvidence.NewHex64(),
            MaterialDigest = AdmittedReportTestEvidence.NewHex64(),
            InputDigest = chain.InputDigest,
            SelectionGeneration = 1,
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

        AssetProcessingJob job = new()
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
        };
        db.AssetProcessingJobs.Add(job);
        await db.SaveChangesAsync();

        await db.CodeAnalysisRuns
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.JobId, jobId));

        db.ChangeTracker.Clear();

        IOptions<CodeAnalysisOptions> codeOptions = MsOptions.Create(new CodeAnalysisOptions
        {
            Enabled = true,
            ServiceKey = SERVICE_KEY,
            PolicyKey = POLICY_KEY,
            PolicyDigest = chain.PolicyDigest,
            Capacity = 1
        });
        IOptions<AssetProcessingOptions> processingOptions = MsOptions.Create(new AssetProcessingOptions());

        IAnalysisCapacityStore capacity = new AnalysisCapacityStore(
            db,
            codeOptions,
            NullLogger<AnalysisCapacityStore>.Instance);

        return new SeedContext(asset, version, runId, chain, capacity, chain.PolicyDigest);
    }
}

