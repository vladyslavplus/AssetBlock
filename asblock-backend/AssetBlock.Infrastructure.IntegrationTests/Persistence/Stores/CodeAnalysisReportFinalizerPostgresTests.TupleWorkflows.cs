using System.Text.Json.Nodes;
using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.SaveAssetDeclaration;
using AssetBlock.Application.UseCases.Assets.SaveAssetDraft;
using AssetBlock.Application.UseCases.Assets.SaveSellerEvidence;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

/// <summary>
/// Decision-basis members changed through their real production workflows while a case is claimed. Each row asserts
/// the outcome the product gives: a save that moves the basis supersedes the active case, while a security recheck
/// keeps the case active and blocks the decision on the old basis. Refused decisions leave no effects.
/// </summary>
public sealed partial class CodeAnalysisReportFinalizerPostgresTests
{
    [Theory]
    [InlineData("declaration")]
    [InlineData("evidence")]
    [InlineData("response")]
    [InlineData("securityRecheck")]
    public async Task TupleWorkflow_WhenMemberChangesWhileCaseIsClaimed_ShouldApplyTheProductionOutcome(string member)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        Guid earlierId = await SeedEarlierRejection(db, submission, reviewer);
        TupleEffects before = await ReadTupleEffectsAsync(db, seed, submission);
        var terminalBefore = await ReadTerminalFingerprintAsync(db, earlierId);

        await RunTupleWorkflowAsync(db, member, seed, submission);

        var superseded = member != "securityRecheck";
        ModerationSubmission refreshed = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        TupleEffects afterWorkflow = await ReadTupleEffectsAsync(db, seed, submission);
        if (superseded)
        {
            afterWorkflow.State.Should().Be(ModerationSubmissionState.SUPERSEDED, "a basis-moving save supersedes the active case");
            afterWorkflow.ClaimHolderId.Should().BeNull();
            afterWorkflow.HistoryRows.Should().Be(before.HistoryRows + 1);
        }
        else
        {
            afterWorkflow.State.Should().Be(ModerationSubmissionState.IN_REVIEW, "a security recheck leaves the claimed case active");
            afterWorkflow.ClaimHolderId.Should().Be(reviewer.Id);
            afterWorkflow.HistoryRows.Should().Be(before.HistoryRows);
        }
        afterWorkflow.CurrentSnapshotId.Should().Be(before.CurrentSnapshotId, "the public pointer must not move on a workflow");
        afterWorkflow.SearchRevision.Should().Be(before.SearchRevision, "the search revision must not move on a workflow");

        ModerationDecisionStatus expectedStatus = superseded ? ModerationDecisionStatus.NOT_ACTIVE : ModerationDecisionStatus.BLOCKED;
        var expectedReason = superseded ? ErrorCodes.ERR_MODERATION_CASE_NOT_ACTIVE : ErrorCodes.ERR_VERSION_FILE_CHECKS_INCOMPLETE;

        ModerationDecisionResult oldBasis = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE));
        oldBasis.Status.Should().Be(expectedStatus);
        oldBasis.ReasonCode.Should().Be(expectedReason);
        TupleEffects afterOld = await ReadTupleEffectsAsync(db, seed, submission);
        afterOld.Should().BeEquivalentTo(afterWorkflow, "a decision on the old basis must leave no effects");

        ModerationDecisionResult refreshedBasis = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, refreshed, reviewer, ModerationDecisionOutcome.APPROVE));
        refreshedBasis.Status.Should().Be(expectedStatus);
        refreshedBasis.ReasonCode.Should().Be(expectedReason);
        TupleEffects afterRefreshed = await ReadTupleEffectsAsync(db, seed, submission);
        afterRefreshed.Should().BeEquivalentTo(afterWorkflow, "a decision on the refreshed basis must leave no effects");

        (await ReadTerminalFingerprintAsync(db, earlierId)).Should().Be(terminalBefore, "the earlier terminal decision must stay untouched");
    }

    /// <summary>
    /// Every production material writer (save, tags, rename, upload) targets the asset-level draft (AssetVersionId null).
    /// A submitted version's workspace is version-scoped, so no production edit moves its material basis. The case must
    /// stay active, its workspace and public pointer must not move, and the version material used for review stays as is.
    /// </summary>
    [Fact]
    public async Task TupleWorkflow_WhenMaterialIsEditedAfterSubmission_ShouldLeaveTheVersionScopedCaseUntouched()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        var assetDraft = new AssetDraftWorkspace
        {
            Id = Guid.NewGuid(),
            AssetId = submission.AssetId,
            AssetVersionId = null,
            WorkspaceVersionScopeKey = DraftWorkspaceVersionScopes.PreUpload,
            ScopeId = Guid.NewGuid(),
            MaterialMetadataHeadRevision = 0,
            SourceDeclarationHeadRevision = 0,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.AssetDraftWorkspaces.Add(assetDraft);
        await db.SaveChangesAsync();
        TupleEffects before = await ReadTupleEffectsAsync(db, seed, submission);
        var caseMaterialHead = (await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId)).MaterialMetadataHeadRevision;

        Result<DraftSaveResult> saved = await new SaveAssetDraftCommandHandler(CoverageStore(db)).Handle(
            new SaveAssetDraftCommand(submission.AssetId, submission.OwnerUserId, Guid.NewGuid(),
                await WorkspaceRevisionAsync(db, assetDraft.Id),
                new SellerDraftMaterialPayload("Edited after submission", null, seed.Source.Asset.CategoryId, [])),
            CancellationToken.None);

        saved.IsSuccess.Should().BeTrue("the asset-level draft accepts the material edit");
        TupleEffects after = await ReadTupleEffectsAsync(db, seed, submission);
        // The asset-level save writes its own audit row (resource id is the asset); nothing else in the case scope moves.
        after.Should().BeEquivalentTo(before with { AuditRows = before.AuditRows + 1 },
            "the active case and its version-scoped material are untouched by the asset-level edit");
        (await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId)).MaterialMetadataHeadRevision
            .Should().Be(caseMaterialHead);

        // The case is still active, so an approval decides on the frozen submitted material, not the newer draft text.
        ModerationDecisionResult approved = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE));
        approved.Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        PublicationSnapshot snapshot = await db.PublicationSnapshots.AsNoTracking().SingleAsync(p => p.AssetId == submission.AssetId);
        snapshot.ApprovedMetadataJson.Should().Contain("Review subject", "the approval publishes the frozen submitted title");
        snapshot.ApprovedMetadataJson.Should().NotContain("Edited after submission", "the newer draft text must stay unpublished");
    }

    /// <summary>
    /// A second analysis run for the same version, enqueued, claimed and finalized through the production enqueuer,
    /// capacity claim and finalizer, becomes the selected report. The claimed case keeps pointing at the first report.
    /// No workflow supersedes it on a selection change, so every decision on it is refused as stale.
    /// </summary>
    [Fact]
    public async Task TupleWorkflow_WhenSelectedReportChangesWhileClaimed_ShouldRefuseDecisionsWithoutEffects()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        Guid earlierId = await SeedEarlierRejection(db, submission, reviewer);
        TupleEffects before = await ReadTupleEffectsAsync(db, seed, submission);
        var terminalBefore = await ReadTerminalFingerprintAsync(db, earlierId);

        Guid selectedReportId = await SelectSecondAnalysisReportAsync(db, seed, submission);

        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == submission.AssetVersionId);
        version.SelectedCodeAnalysisReportId.Should().Be(selectedReportId, "the second finalized run is the selected report");
        selectedReportId.Should().NotBe(submission.CodeAnalysisReportHeaderId!.Value);
        ModerationSubmission refreshed = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        TupleEffects afterWorkflow = await ReadTupleEffectsAsync(db, seed, submission);
        afterWorkflow.State.Should().Be(ModerationSubmissionState.IN_REVIEW, "a selection change does not supersede the claimed case");
        afterWorkflow.ClaimHolderId.Should().Be(reviewer.Id);
        afterWorkflow.CurrentSnapshotId.Should().Be(before.CurrentSnapshotId);
        afterWorkflow.SearchRevision.Should().Be(before.SearchRevision);

        ModerationDecisionRequest oldInput = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        ModerationDecisionRequest selectedInput = oldInput with { Basis = oldInput.Basis with { AnalysisReportId = selectedReportId } };
        foreach (ModerationDecisionRequest input in new[] { oldInput, DecisionRequest(seed, refreshed, reviewer, ModerationDecisionOutcome.APPROVE), selectedInput })
        {
            ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(input);
            result.Status.Should().Be(ModerationDecisionStatus.STALE);
            result.ReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_CASE_STALE);
            (await ReadTupleEffectsAsync(db, seed, submission)).Should().BeEquivalentTo(afterWorkflow, "a refused decision must leave no effects");
        }

        (await ReadTerminalFingerprintAsync(db, earlierId)).Should().Be(terminalBefore, "the earlier terminal decision must stay untouched");
    }

    /// <summary>
    /// No product workflow rewrites a version's content hash after save. The EF after-save guard refuses the write,
    /// so a content change can only reach a case through a new version row.
    /// </summary>
    [Fact]
    public async Task TupleWorkflow_WhenContentHashChangesOnAnExistingVersion_ShouldRefuseTheWrite()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (_, ModerationSubmission submission, _) = await SeedClaimedDecision(db);
        AssetVersion version = await db.AssetVersions.SingleAsync(v => v.Id == submission.AssetVersionId);
        version.ContentSha256 = new string('c', 64);

        Func<Task> write = () => db.SaveChangesAsync();
        await write.Should().ThrowAsync<InvalidOperationException>("the content hash is write-once after save; no product workflow rewrites it");
    }

    /// <summary>
    /// The version id is the identity of a case. No workflow re-points a submitted case to another version, so a basis
    /// naming another version is refused as stale with no effects.
    /// </summary>
    [Fact]
    public async Task TupleWorkflow_WhenBasisNamesAnotherVersion_ShouldRefuseAsStale()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        TupleEffects before = await ReadTupleEffectsAsync(db, seed, submission);
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        ModerationDecisionRequest otherVersion = input with { Basis = input.Basis with { VersionId = Guid.NewGuid() } };

        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(otherVersion);

        result.Status.Should().Be(ModerationDecisionStatus.STALE);
        result.ReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_CASE_STALE);
        (await ReadTupleEffectsAsync(db, seed, submission)).Should().BeEquivalentTo(before, "a refused decision must leave no effects");
    }

    /// <summary>
    /// The report id is part of the decision basis. A basis naming a report that is neither the case's report nor the
    /// selected one is refused as stale with no effects, while the case still points at the selected report.
    /// </summary>
    [Fact]
    public async Task TupleWorkflow_WhenBasisNamesAnotherReport_ShouldRefuseAsStale()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        TupleEffects before = await ReadTupleEffectsAsync(db, seed, submission);
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        ModerationDecisionRequest otherReport = input with { Basis = input.Basis with { AnalysisReportId = Guid.NewGuid() } };

        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(otherReport);

        result.Status.Should().Be(ModerationDecisionStatus.STALE);
        result.ReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_CASE_STALE);
        (await ReadTupleEffectsAsync(db, seed, submission)).Should().BeEquivalentTo(before, "a refused decision must leave no effects");
    }

    /// <summary>
    /// The policy version is a static constant. No workflow changes it on a case, so the case must carry the current constant.
    /// </summary>
    [Fact]
    public async Task TupleWorkflow_WhenPolicyVersionIsCurrent_ShouldMatchTheReviewAnalysisPolicyConstant()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (_, ModerationSubmission submission, _) = await SeedClaimedDecision(db);

        submission.PolicyVersion.Should().Be(ReviewAnalysisPolicy.KEY, "no workflow changes the policy version of a case");
    }

    private static async Task<Guid> SelectSecondAnalysisReportAsync(ApplicationDbContext db, ResponseReviewSeed seed, ModerationSubmission submission)
    {
        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == submission.AssetVersionId);
        CodeAnalysisRun first = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.AssetVersionId == version.Id);
        first.State.Should().Be(CodeAnalysisRunState.COMPLETED, "the first run must be settled before the second enqueue");
        var enqueuer = new AnalysisRunEnqueuer(db,
            MsOptions.Create(new CodeAnalysisOptions
            {
                Enabled = true,
                ServiceKey = SERVICE_KEY,
                PolicyKey = POLICY_KEY,
                PolicyDigest = seed.Source.PolicyDigest,
                Capacity = 1
            }),
            MsOptions.Create(new AssetProcessingOptions()),
            NullLogger<AnalysisRunEnqueuer>.Instance);
        AnalysisRunEnqueueResult enqueued = await enqueuer.Enqueue(version.AssetId, version.Id, null, first.ArchiveEvidenceId, first.MalwareEvidenceId, 1, version.ContentSha256, DateTimeOffset.UtcNow);
        enqueued.Rejection.Should().Be(AnalysisEnqueueRejection.NONE, "the production enqueue must accept a second run for the same version");
        enqueued.IsReplay.Should().BeFalse("a replay returns the first run instead of creating a second one");
        enqueued.AnalysisRunId.Should().NotBe(first.Id);

        CodeAnalysisRun second = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.AssetVersionId == version.Id && r.Id != first.Id);
        // The first claim's slot stays reserved after its run is finalized. The worker releases it with the
        // confirmed disposition before the next claim, as in production.
        AnalysisCapacitySlot firstSlot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync(s => s.State != AnalysisCapacitySlotState.FREE);
        (await seed.Source.Capacity.ReleaseReservation(firstSlot.Id, firstSlot.LeaseToken!, AnalysisExecutionDisposition.CONFIRMED_TERMINATED))
            .Should().BeTrue("the settled first run releases its slot");
        await seed.Source.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob job = (await seed.Source.Capacity.ClaimWithCapacity(1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "reselect-worker")).Single();
        job.JobId.Should().Be(second.JobId!.Value, "the claim must pick the second run's job");

        JsonObject body = JsonNode.Parse(BuildResponseBody(second, seed.Source.Version, seed.Source.Asset, seed.Source.Chain, seed.Source.PolicyDigest))!.AsObject();
        var finalizer = new CodeAnalysisReportFinalizer(db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        (await finalizer.FinalizeConfirmed(job.JobId, job.LeaseToken, PythonAnalysisEnvelope.RegenerateDerivedIdentity(body).ToJsonString()))
            .Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();

        return await db.CodeAnalysisReportHeaders.AsNoTracking().Where(h => h.AnalysisRunId == second.Id).Select(h => h.Id).SingleAsync();
    }

    private static async Task RunTupleWorkflowAsync(ApplicationDbContext db, string member, ResponseReviewSeed seed, ModerationSubmission submission)
    {
        Guid ownerId = seed.Source.Asset.AuthorId;
        ModerationFoundationStore store = CoverageStore(db);
        switch (member)
        {
            case "declaration":
                {
                    var revision = await WorkspaceRevisionAsync(db, submission.WorkspaceId);
                    Result<DraftSaveResult> saved = await new SaveAssetDeclarationCommandHandler(store).Handle(
                        new SaveAssetDeclarationCommand(seed.Source.Asset.Id, seed.Source.Version.Id, ownerId, Guid.NewGuid(), revision,
                            new SourceDeclarationPayload("Revised own contribution summary.", null, null, true, "1", [])),
                        CancellationToken.None);
                    saved.IsSuccess.Should().BeTrue("the production declaration save must succeed");
                    break;
                }
            case "evidence":
                {
                    SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, ownerId, 1))!;
                    var command = new SaveSellerEvidenceCommand(ownerId, seed.Source.Asset.Id, seed.Source.Version.Id, current.WorkspaceId,
                        seed.ReportId, null, null, "Revised evidence text", current.WorkspaceRevision, current.EvidenceRevision, Guid.NewGuid());
                    (await new SaveSellerEvidenceCommandHandler(store).Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue("the production evidence save must succeed");
                    break;
                }
            case "response":
                {
                    SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, ownerId, 1))!;
                    var command = new SaveSellerEvidenceCommand(ownerId, seed.Source.Asset.Id, seed.Source.Version.Id, current.WorkspaceId,
                        seed.ReportId, seed.Findings[0], SellerResponseType.NO_EVIDENCE, "Revised response text", current.WorkspaceRevision, current.EvidenceRevision, Guid.NewGuid());
                    (await new SaveSellerEvidenceCommandHandler(store).Handle(command, CancellationToken.None)).IsSuccess.Should().BeTrue("the production response save must succeed");
                    break;
                }
            case "securityRecheck":
                {
                    AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.Source.Version.Id);
                    var provenance = new SecurityProvenanceStore(db, MsOptions.Create(new AssetProcessingOptions()),
                        Substitute.For<IAnalysisRunEnqueuer>(), NullLogger<SecurityProvenanceStore>.Instance);
                    SecurityRecheckStartResult started = await provenance.StartSecurityRecheck(new SecurityRecheckStartRequest(
                        seed.Source.Asset.Id, version.Id, version.ContentSha256, AssetProcessingDefaults.MALWARE_POLICY_VERSION,
                        AssetProcessingDefaults.DEFINITION_VERSION, new string('e', 64), Guid.NewGuid()));
                    started.IsReplay.Should().BeFalse();
                    (await db.SecurityRechecks.AsNoTracking().CountAsync(r => r.AssetVersionId == version.Id)).Should().BeGreaterThan(0);
                    break;
                }
            default:
                throw new ArgumentOutOfRangeException(nameof(member), member, "Unknown decision-basis member.");
        }
    }

    private static async Task<long> WorkspaceRevisionAsync(ApplicationDbContext db, Guid workspaceId) =>
        (await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == workspaceId)).WorkspaceRevision;

    private static async Task<Guid> SeedEarlierRejection(ApplicationDbContext db, ModerationSubmission submission, User reviewer)
    {
        var earlier = new ModerationSubmission
        {
            Id = Guid.NewGuid(),
            AssetId = submission.AssetId,
            AssetVersionId = submission.AssetVersionId,
            WorkspaceId = submission.WorkspaceId,
            WorkspaceVersionScopeKey = submission.WorkspaceVersionScopeKey,
            OwnerUserId = submission.OwnerUserId,
            ContentSha256 = submission.ContentSha256,
            CodeAnalysisReportHeaderId = submission.CodeAnalysisReportHeaderId,
            DeclarationRevision = submission.DeclarationRevision,
            MaterialMetadataRevision = submission.MaterialMetadataRevision,
            SellerEvidenceRevision = submission.SellerEvidenceRevision,
            SellerEvidenceDigest = submission.SellerEvidenceDigest,
            PolicyVersion = submission.PolicyVersion,
            State = ModerationSubmissionState.REJECTED,
            CaseRevision = 1,
            CreatedAt = submission.CreatedAt.AddDays(-1)
        };
        db.ModerationSubmissions.Add(earlier);
        db.ModerationDecisionRecords.Add(new ModerationDecisionRecord
        {
            Id = Guid.NewGuid(),
            SubmissionId = earlier.Id,
            AssetId = earlier.AssetId,
            AssetVersionId = earlier.AssetVersionId,
            ModeratorUserId = reviewer.Id,
            Outcome = ModerationSubmissionState.REJECTED,
            CaseRevision = 1,
            Message = "Earlier rejection.",
            CreatedAt = earlier.CreatedAt
        });
        await db.SaveChangesAsync();
        return earlier.Id;
    }

    private static async Task<string> ReadTerminalFingerprintAsync(ApplicationDbContext db, Guid earlierId)
    {
        ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == earlierId);
        ModerationDecisionRecord decision = await db.ModerationDecisionRecords.AsNoTracking().SingleAsync(d => d.SubmissionId == earlierId);
        return string.Join('|', row.State, row.CaseRevision, decision.Outcome, decision.CaseRevision, decision.Message, decision.ModeratorUserId);
    }

    private sealed record TupleEffects(ModerationSubmissionState State, Guid? ClaimHolderId, long CaseRevision, long WorkspaceRevision,
        int HistoryRows, int DecisionRows, int SnapshotRows, Guid? CurrentSnapshotId, long SearchRevision, int OutboxRows, int AuditRows);

    private static async Task<TupleEffects> ReadTupleEffectsAsync(ApplicationDbContext db, ResponseReviewSeed seed, ModerationSubmission submission)
    {
        ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        Asset asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId);
        var auditKeys = new List<string> { submission.AssetId.ToString(), submission.AssetVersionId.ToString(), submission.Id.ToString(), seed.ReportId.ToString() };
        return new TupleEffects(
            row.State,
            row.ClaimHolderId,
            row.CaseRevision,
            workspace.WorkspaceRevision,
            await db.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == submission.Id),
            await db.ModerationDecisionRecords.CountAsync(d => d.AssetId == submission.AssetId),
            await db.PublicationSnapshots.CountAsync(p => p.AssetId == submission.AssetId),
            asset.CurrentPublicationSnapshotId,
            asset.SearchRevision,
            await db.OutboxMessages.CountAsync(o => (o.Type == OutboxMessageTypes.MODERATION_DECISION || o.Type == OutboxMessageTypes.MODERATION_LIFECYCLE)
                && (o.Payload.Contains(submission.AssetId.ToString()) || o.Payload.Contains(submission.Id.ToString()))),
            await db.AuditLogs.CountAsync(a => a.ResourceId != null && auditKeys.Contains(a.ResourceId)));
    }
}
