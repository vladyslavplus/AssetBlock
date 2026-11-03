using System.Text.Json;
using System.Text.Json.Nodes;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

public sealed partial class CodeAnalysisReportFinalizerPostgresTests
{
    private sealed record FinalizationEffects(
        int Headers,
        int Stages,
        int Findings,
        int Admissions,
        int FinalizedAudits,
        int Decisions,
        int Snapshots,
        int Outbox,
        int Notifications,
        Guid? SelectedReportId,
        Guid? PointerSnapshotId,
        ModerationSubmissionState State);

    /// <summary>
    /// A duplicate delivery of an accepted finalization, arriving after the moderator decided the case, returns the
    /// frozen report and adds no report, stage, finding, admission, decision, snapshot, audit, outbox or notification row.
    /// </summary>
    [Fact]
    public async Task FinalizeConfirmed_WhenDuplicateDeliveryFollowsDecision_ShouldCreateNoSecondEffects()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        ModerationDecisionResult decided = await CoverageStore(db).DecideModerationCase(
            DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE));
        decided.Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        Guid jobId = await db.CodeAnalysisRuns.AsNoTracking().Where(r => r.Id == seed.Source.RunId).Select(r => r.JobId!.Value).SingleAsync();
        FinalizationEffects before = await ReadFinalizationEffectsAsync(db, submission);
        before.State.Should().Be(ModerationSubmissionState.APPROVED, "the decision is recorded before the duplicate arrives");

        var finalizer = new CodeAnalysisReportFinalizer(db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        AssetProcessingJobOutcome duplicate = await finalizer.FinalizeConfirmed(jobId, Guid.NewGuid(), seed.AcceptedBody, CancellationToken.None);

        duplicate.Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>()
            .Which.JobStatus.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        (await ReadFinalizationEffectsAsync(db, submission)).Should().BeEquivalentTo(before,
            "a duplicate delivery after the decision must not add a report, decision, snapshot, notification or outbox row");
    }

    /// <summary>
    /// A late analysis result for an older version of the same asset, finalized after a newer version was approved and
    /// published, is stored for its own version. It never moves the public pointer, the newer version's selected report,
    /// or the decision history.
    /// </summary>
    [Fact]
    public async Task FinalizeConfirmed_WhenOlderVersionResultArrivesAfterNewerApproval_ShouldLeavePublicStateUntouched()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        await db.AssetVersions.Where(v => v.Id == submission.AssetVersionId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.VersionNumber, 3));
        ModerationDecisionResult decided = await CoverageStore(db).DecideModerationCase(
            DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE));
        decided.Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        FinalizationEffects before = await ReadFinalizationEffectsAsync(db, submission);
        before.PointerSnapshotId.Should().NotBeNull("the newer version is public");

        (AssetVersion olderVersion, CodeAnalysisRun olderRun, Guid olderJobId, AdmittedReportTestEvidence.Chain olderChain) =
            await AddQueuedOlderVersionAsync(db, seed.Source.Asset);
        AssetVersion approvedVersion = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == submission.AssetVersionId);
        PublicationSnapshot publicSnapshot = await db.PublicationSnapshots.AsNoTracking()
            .SingleAsync(snapshot => snapshot.Id == before.PointerSnapshotId);
        publicSnapshot.AssetVersionId.Should().Be(approvedVersion.Id, "the public snapshot belongs to the approved version");
        approvedVersion.VersionNumber.Should().Be(3, "the approved publication is explicitly the newer fixture version");
        olderRun.AssetVersionId.Should().Be(olderVersion.Id);
        olderVersion.AssetId.Should().Be(approvedVersion.AssetId, "the late run belongs to the same asset");
        olderVersion.VersionNumber.Should().BeLessThan(approvedVersion.VersionNumber,
            "the finalized result must belong to a persisted older version than the approved publication");

        // The first claim keeps its capacity slot after its run is finalized. The worker releases it before the next claim.
        AnalysisCapacitySlot firstSlot = await db.AnalysisCapacitySlots.AsNoTracking().SingleAsync(s => s.State != AnalysisCapacitySlotState.FREE);
        (await seed.Source.Capacity.ReleaseReservation(firstSlot.Id, firstSlot.LeaseToken!, AnalysisExecutionDisposition.CONFIRMED_TERMINATED))
            .Should().BeTrue("the settled first run releases its slot");
        await seed.Source.Capacity.EnsureSlots(SERVICE_KEY, 1);
        ClaimedAssetProcessingJob claimed = (await seed.Source.Capacity.ClaimWithCapacity(
            1, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(18), TimeSpan.FromMinutes(2), "late-worker")).Single();
        claimed.JobId.Should().Be(olderJobId, "the claim must pick the older version's job");

        CodeAnalysisRun olderRunRow = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == olderRun.Id);
        JsonObject body = JsonNode.Parse(BuildResponseBody(olderRunRow, olderVersion, seed.Source.Asset, olderChain, olderChain.PolicyDigest))!.AsObject();
        var finalizer = new CodeAnalysisReportFinalizer(db, new CodeAnalysisReportValidator(), NullLogger<CodeAnalysisReportFinalizer>.Instance);
        (await finalizer.FinalizeConfirmed(claimed.JobId, claimed.LeaseToken, PythonAnalysisEnvelope.RegenerateDerivedIdentity((JsonObject)body).ToJsonString()))
            .Should().BeOfType<AssetProcessingJobOutcome.AtomicCommitted>();

        CodeAnalysisRun finalizedOlderRun = await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == olderRun.Id);
        finalizedOlderRun.State.Should().Be(CodeAnalysisRunState.COMPLETED, "the late result was accepted for its own version");

        FinalizationEffects after = await ReadFinalizationEffectsAsync(db, submission);
        after.PointerSnapshotId.Should().Be(before.PointerSnapshotId, "a late older result never moves the public pointer");
        after.SelectedReportId.Should().Be(before.SelectedReportId, "the newer version keeps its selected report");
        after.Decisions.Should().Be(before.Decisions, "no decision is created by a finalization");
        after.Snapshots.Should().Be(before.Snapshots, "no publication snapshot is created by a finalization");
        after.State.Should().Be(ModerationSubmissionState.APPROVED, "the newer case stays approved");
    }

    private static async Task<FinalizationEffects> ReadFinalizationEffectsAsync(ApplicationDbContext db, ModerationSubmission submission)
    {
        ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        Asset asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId);
        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == submission.AssetVersionId);
        return new FinalizationEffects(
            await db.CodeAnalysisReportHeaders.CountAsync(),
            await db.CodeAnalysisReportStages.CountAsync(),
            await db.CodeAnalysisReportFindings.CountAsync(),
            await db.CodeAnalysisReportAdmissions.CountAsync(),
            await db.AuditLogs.CountAsync(a => a.Action == "CODE_ANALYSIS_FINALIZED"),
            await db.ModerationDecisionRecords.CountAsync(d => d.SubmissionId == submission.Id),
            await db.PublicationSnapshots.CountAsync(p => p.AssetId == submission.AssetId),
            await db.OutboxMessages.CountAsync(),
            await db.UserNotifications.CountAsync(),
            version.SelectedCodeAnalysisReportId,
            asset.CurrentPublicationSnapshotId,
            row.State);
    }

    /// <summary>
    /// Adds an older, non-current version to an existing asset with its own queued run and job, pinned to the same policy row.
    /// The shape matches <see cref="SeedQueued"/> for a version that is not the one the asset already has.
    /// </summary>
    private static async Task<(AssetVersion Version, CodeAnalysisRun Run, Guid JobId, AdmittedReportTestEvidence.Chain Chain)> AddQueuedOlderVersionAsync(
        ApplicationDbContext db, Asset asset)
    {
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id, versionNumber: 2, isCurrent: false, processingStatus: AssetVersionProcessingStatus.READY);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        Guid policyVersionId = await db.CodeAnalysisPolicyVersions.AsNoTracking()
            .Where(p => p.PolicyKey == ReviewAnalysisPolicy.KEY && p.PolicyDigest == ReviewAnalysisPolicy.DIGEST)
            .Select(p => p.Id).SingleAsync();
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain() with
        {
            PolicyVersionId = policyVersionId,
            PolicyDigest = ReviewAnalysisPolicy.DIGEST
        };
        AdmittedReportTestEvidence.SeedObservedProof(db, version, chain);
        await db.SaveChangesAsync();

        var jobId = Guid.NewGuid();
        var run = new CodeAnalysisRun
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

        return (version, run, jobId, chain);
    }
}
