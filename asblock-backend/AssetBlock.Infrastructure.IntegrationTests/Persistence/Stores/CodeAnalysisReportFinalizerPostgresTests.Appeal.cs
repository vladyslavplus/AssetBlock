using AssetBlock.Application.UseCases.Assets.SaveSellerEvidence;
using AssetBlock.Application.UseCases.Assets.SubmitAssetVersion;
using AssetBlock.Application.UseCases.Assets.WithdrawSubmission;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NSubstitute;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

public sealed partial class CodeAnalysisReportFinalizerPostgresTests
{
    [Fact]
    public async Task Appeal_WhenOpenedEventIsDispatched_ShouldExcludePriorReviewerAndReplayOnce()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        User other = await CoverageModerator(db);
        User promoted = await CoverageModerator(db, AppRoles.USER);
        User revoked = await CoverageModerator(db);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "PRIVATE APPEAL SENTINEL", new string('a', 64));
        Guid appealId = (await CoverageStore(db).RequestAppeal(request)).SubmissionId!.Value;
        await db.Users.Where(u => u.Id == submission.OwnerUserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.MODERATOR));
        await db.Users.Where(u => u.Id == promoted.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.MODERATOR));
        await db.Users.Where(u => u.Id == revoked.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        OutboxMessage message = (await db.OutboxMessages.AsNoTracking().Where(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE).ToListAsync())
            .Single(m => m.Payload.Contains(appealId.ToString(), StringComparison.OrdinalIgnoreCase));
        message.Payload.Should().NotContain("PRIVATE APPEAL SENTINEL");
        var handler = new ModerationLifecycleOutboxHandler(db, TimeProvider.System);
        await handler.Handle(message, CancellationToken.None);
        await handler.Handle(message, CancellationToken.None);
        List<UserNotification> rows = await db.UserNotifications.AsNoTracking().Where(n => n.Kind == NotificationKind.MODERATION_SUBMITTED).ToListAsync();
        rows.Select(n => n.RecipientUserId).Should().BeEquivalentTo([submission.OwnerUserId, other.Id, promoted.Id]);
        rows.Should().OnlyContain(n => !n.MetadataJson.Contains("PRIVATE APPEAL SENTINEL"));
        rows.Should().NotContain(n => n.RecipientUserId == reviewer.Id);
        rows.Should().NotContain(n => n.RecipientUserId == revoked.Id);
    }
    [Theory]
    [InlineData(ModerationDecisionOutcome.REJECT, NotificationKind.MODERATION_APPEAL_UPHELD)]
    [InlineData(ModerationDecisionOutcome.APPROVE, NotificationKind.MODERATION_APPROVED)]
    public async Task Appeal_WhenDecidedEventIsDispatched_ShouldNotifyCurrentOwner(ModerationDecisionOutcome outcome, NotificationKind kind)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "PRIVATE APPEAL SENTINEL", new string('a', 64));
        Guid appealId = (await CoverageStore(db).RequestAppeal(request)).SubmissionId!.Value;
        User other = await CoverageModerator(db);
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appealId);
        (await CoverageStore(db).ChangeCaseClaim(new(other.Id, appealId, Guid.NewGuid(), appeal.CaseRevision, true, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appealId);
        ModerationDecisionResult decision = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, appeal, other, outcome));
        decision.Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        OutboxMessage message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == ModerationEventIdentity.Decision(decision.Saved!.DecisionId, true));
        message.Payload.Should().Contain("\"isAppeal\":true").And.NotContain("PRIVATE APPEAL SENTINEL");
        User newOwner = await CoverageModerator(db, AppRoles.USER);
        await db.Assets.Where(a => a.Id == submission.AssetId).ExecuteUpdateAsync(s => s.SetProperty(a => a.AuthorId, newOwner.Id));
        IRealtimeNotificationPublisher publisher = Substitute.For<IRealtimeNotificationPublisher>();
        var handler = new ModerationDecisionOutboxHandler(publisher, Substitute.For<ICacheService>(), Substitute.For<IPublicationEmbeddingEnqueuer>(), new AssetStore(db));
        await handler.Handle(message, CancellationToken.None);
        await handler.Handle(message, CancellationToken.None);
        await publisher.Received(2).DeliverPersistedNotification(message.Id, newOwner.Id, kind, NotificationHubMethods.MODERATION_CASE_CHANGED,
            Arg.Is<string>(text => text.Contains("\"isAppeal\":true") && !text.Contains("PRIVATE APPEAL SENTINEL")), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task Appeal_WhenSellerHasNoSubmission_ShouldNotOfferAppeal()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        SellerVersionReviewDto review = (await CoverageStore(db).GetOwnerVersionReview(seed.Source.Version.Id, seed.Source.Asset.AuthorId))!;
        review.CanAppeal.Should().BeFalse();
        review.Appeal.Should().BeNull();
    }
    [Fact]
    public async Task Appeal_WhenSellerReadsRejectedAndPendingCase_ShouldMatchRequestEligibility()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        ModerationFoundationStore store = CoverageStore(db);
        SellerVersionReviewDto? before = await store.GetOwnerVersionReview(submission.AssetVersionId, submission.OwnerUserId);
        before!.CanAppeal.Should().BeTrue();
        before.Appeal.Should().BeNull();
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        (await store.RequestAppeal(request)).Status.Should().Be(GuardedSubmissionStatus.CREATED);
        SellerVersionReviewDto? after = await store.GetOwnerVersionReview(submission.AssetVersionId, submission.OwnerUserId);
        after!.CanAppeal.Should().BeFalse();
        after.Appeal!.State.Should().Be(SellerAppealState.PENDING_REVIEW);
        after.Decision.Should().BeNull();
        after.BlockedReasons.Should().NotContain(ErrorCodes.ERR_MODERATION_REJECTION_FINAL);
        (await store.GetOwnerVersionReview(submission.AssetVersionId, reviewer.Id)).Should().BeNull();
    }
    [Theory]
    [InlineData("submitted", SellerAppealState.PENDING_REVIEW)]
    [InlineData("inreview", SellerAppealState.PENDING_REVIEW)]
    [InlineData("withdrawn", SellerAppealState.WITHDRAWN)]
    [InlineData("rejected", SellerAppealState.UPHELD)]
    [InlineData("approved", SellerAppealState.APPROVED)]
    public async Task Appeal_WhenSellerReadsAppeal_ShouldProjectSafeState(string state, SellerAppealState expected)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Private appeal marker", new string('a', 64));
        ModerationFoundationStore store = CoverageStore(db);
        GuardedSubmissionResult created = await store.RequestAppeal(request);
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        User other = await CoverageModerator(db);
        if (state is "inreview" or "rejected" or "approved")
        {
            (await store.ChangeCaseClaim(new(other.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, true, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
            appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appeal.Id);
        }
        if (state is "rejected" or "approved")
        {
            ModerationDecisionOutcome outcome = state == "approved" ? ModerationDecisionOutcome.APPROVE : ModerationDecisionOutcome.REJECT;
            (await store.DecideModerationCase(DecisionRequest(seed, appeal, other, outcome))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        }
        if (state == "withdrawn")
        { (await new WithdrawSubmissionCommandHandler(store).Handle(new WithdrawSubmissionCommand(submission.OwnerUserId, appeal.Id, appeal.CaseRevision, Guid.NewGuid()), CancellationToken.None)).IsSuccess.Should().BeTrue(); }
        SellerVersionReviewDto review = (await store.GetOwnerVersionReview(submission.AssetVersionId, submission.OwnerUserId))!;
        review.CanAppeal.Should().BeFalse();
        review.Appeal!.State.Should().Be(expected);
        review.Appeal.RequestedAt.Should().Be(appeal.CreatedAt);
        if (state is "rejected" or "approved")
        { review.Appeal.DecidedAt.Should().NotBeNull(); review.Decision.Should().NotBeNull(); }
        else
        { review.Appeal.DecidedAt.Should().BeNull(); review.Decision.Should().BeNull(); }
        var json = System.Text.Json.JsonSerializer.Serialize(review, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        json.Should().NotContain(reviewer.Id.ToString()).And.NotContain(other.Id.ToString()).And.NotContain("Private appeal marker")
            .And.NotContain("claimHolder").And.NotContain("dispositions").And.NotContain("exclusion").And.NotContain("score");
    }
    [Fact]
    public async Task Appeal_WhenRejected_ShouldCreateLinkedCaseAndReplay()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status
            .Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        GuardedSubmissionResult created = await CoverageStore(db).RequestAppeal(request);
        created.Status.Should().Be(GuardedSubmissionStatus.CREATED);
        GuardedSubmissionResult replay = await CoverageStore(db).RequestAppeal(request);
        replay.Status.Should().Be(GuardedSubmissionStatus.REPLAYED);
        replay.SubmissionId.Should().Be(created.SubmissionId);
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        appeal.Kind.Should().Be(ModerationSubmissionKind.APPEAL);
        appeal.PreviousSubmissionId.Should().Be(submission.Id);
        appeal.AppealOfDecisionId.Should().Be((await db.ModerationDecisionRecords.AsNoTracking().SingleAsync(d => d.SubmissionId == submission.Id)).Id);
        appeal.State.Should().Be(ModerationSubmissionState.SUBMITTED);
        appeal.ClaimHolderId.Should().BeNull();
        appeal.CaseRevision.Should().Be(workspace.CaseRevision + 1);
        (await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == workspace.Id)).CaseRevision.Should().Be(workspace.CaseRevision + 1);
        (await db.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == appeal.Id && h.State == ModerationSubmissionState.SUBMITTED)).Should().Be(1);
        appeal.AppealRationale.Should().Be("Review again.");
        AuditLog audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.APPEAL_OPENED && a.ResourceId == appeal.Id.ToString());
        audit.MetadataJson.Should().NotBeNull().And.NotContain("Review again.");
        (await db.OutboxMessages.AsNoTracking().Select(m => m.Payload).ToListAsync()).Should().OnlyContain(payload => !payload.Contains("Review again."));
        (await db.JsonMutationIdempotencyRecords.AsNoTracking().Where(r => r.OperationId == request.OperationId).Select(r => r.ResultJson).SingleAsync()).Should().NotContain("Review again.");
        await db.Assets.Where(a => a.Id == submission.AssetId).ExecuteUpdateAsync(s => s.SetProperty(a => a.AuthorId, reviewer.Id));
        (await CoverageStore(db).RequestAppeal(request)).BlockedReasonCode.Should().Be(ErrorCodes.ERR_FORBIDDEN);
    }
    [Theory]
    [InlineData(ModerationDecisionOutcome.APPROVE)]
    [InlineData(ModerationDecisionOutcome.REQUEST_CHANGES)]
    public async Task Appeal_WhenOutcomeIsNotRejected_ShouldRefuse(ModerationDecisionOutcome outcome)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, outcome))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        (await CoverageStore(db).GetOwnerVersionReview(submission.AssetVersionId, submission.OwnerUserId))!.CanAppeal.Should().BeFalse();
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        GuardedSubmissionResult result = await CoverageStore(db).RequestAppeal(request);
        result.BlockedReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_APPEAL_NOT_ALLOWED);
    }

    [Fact]
    public async Task Appeal_WhenRequestConflicts_ShouldNotCreateExtraCase()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        (await CoverageStore(db).RequestAppeal(request)).Status.Should().Be(GuardedSubmissionStatus.CREATED);
        (await CoverageStore(db).RequestAppeal(request with { RequestDigest = new string('b', 64) })).BlockedReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT);
        (await CoverageStore(db).RequestAppeal(request with { OperationId = Guid.NewGuid() })).BlockedReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_APPEAL_EXISTS);
        await db.ModerationSubmissions.Where(s => s.Kind == ModerationSubmissionKind.APPEAL).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ModerationSubmissionState.WITHDRAWN));
        (await CoverageStore(db).RequestAppeal(request with { OperationId = Guid.NewGuid() })).BlockedReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_APPEAL_EXISTS);
        (await db.ModerationSubmissions.CountAsync(s => s.Kind == ModerationSubmissionKind.APPEAL)).Should().Be(1);
    }

    [Fact]
    public async Task Appeal_WhenReviewerIsNotOwner_ShouldDeny()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(reviewer.Id, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        (await CoverageStore(db).RequestAppeal(request)).BlockedReasonCode.Should().Be(ErrorCodes.ERR_FORBIDDEN);
        (await db.ModerationSubmissions.CountAsync(s => s.Kind == ModerationSubmissionKind.APPEAL)).Should().Be(0);
    }
    [Theory]
    [InlineData("content")]
    [InlineData("report")]
    [InlineData("declaration")]
    [InlineData("material")]
    [InlineData("policy")]
    [InlineData("policyDigest")]
    [InlineData("restricted")]
    public async Task Appeal_WhenBasisOrSecurityChanges_ShouldRefuse(string changed)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        if (changed == "content")
        { await db.AssetVersions.Where(v => v.Id == submission.AssetVersionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.ContentSha256, new string('c', 64))); }
        if (changed == "report")
        { await db.AssetVersions.Where(v => v.Id == submission.AssetVersionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.SelectedCodeAnalysisReportId, (Guid?)null)); }
        if (changed == "declaration")
        { await db.AssetDraftWorkspaces.Where(w => w.Id == submission.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(w => w.SourceDeclarationHeadRevision, w => w.SourceDeclarationHeadRevision + 1)); }
        if (changed == "material")
        { await db.AssetDraftWorkspaces.Where(w => w.Id == submission.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(w => w.MaterialMetadataHeadRevision, w => w.MaterialMetadataHeadRevision + 1)); }
        if (changed == "policy")
        { await db.ModerationSubmissions.Where(v => v.Id == submission.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.PolicyVersion, "different-policy")); }
        if (changed == "policyDigest")
        { await db.CodeAnalysisRuns.Where(r => r.Id == seed.Source.RunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.PolicyDigest, new string('d', 64))); }
        if (changed == "restricted")
        {
            await db.AssetVersions.Where(v => v.Id == submission.AssetVersionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.SafetyRestriction, AssetVersionSafetyRestriction.INFECTED)
                .SetProperty(v => v.SafetyRestrictionReason, "test restriction").SetProperty(v => v.SafetyRestrictedAt, DateTimeOffset.UtcNow));
        }
        (await CoverageStore(db).GetOwnerVersionReview(submission.AssetVersionId, submission.OwnerUserId))!.CanAppeal.Should().BeFalse();
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        GuardedSubmissionResult result = await CoverageStore(db).RequestAppeal(request);
        result.BlockedReasonCode.Should().Be(changed == "restricted" ? ErrorCodes.ERR_VERSION_FILE_CHECKS_INCOMPLETE : ErrorCodes.ERR_MODERATION_APPEAL_BASIS_CHANGED);
        (await db.ModerationSubmissions.CountAsync(s => s.Kind == ModerationSubmissionKind.APPEAL)).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Appeal_WhenWorkspaceRevisionIsStale_ShouldRefuse(bool workspaceStale)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision + (workspaceStale ? 1 : 0), workspace.CaseRevision + (workspaceStale ? 0 : 1), Guid.NewGuid(), "Review again.", new string('a', 64));
        (await CoverageStore(db).RequestAppeal(request)).BlockedReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task Appeal_WhenTwoRequestsRace_ShouldCreateOne(int _)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        await using ApplicationDbContext blocker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction held = await HoldDecisionAsset(blocker, submission.AssetId);
        await left.Database.OpenConnectionAsync();
        await right.Database.OpenConnectionAsync();
        Task<GuardedSubmissionResult> first = CoverageStore(left).RequestAppeal(request);
        Task<GuardedSubmissionResult> second = CoverageStore(right).RequestAppeal(request with { OperationId = Guid.NewGuid() });
        await Task.Delay(200);
        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        await held.CommitAsync();
        GuardedSubmissionResult[] results = await Task.WhenAll(first, second);
        results.Count(r => r.Status == GuardedSubmissionStatus.CREATED).Should().Be(1);
        results.Count(r => r.BlockedReasonCode == ErrorCodes.ERR_MODERATION_APPEAL_EXISTS).Should().Be(1);
        (await db.ModerationSubmissions.CountAsync(s => s.Kind == ModerationSubmissionKind.APPEAL)).Should().Be(1);
    }
    private sealed class FailAppealOutbox : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added))
            { throw new DbUpdateException("Outbox write failed."); }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Appeal_WhenAuditOrOutboxFails_ShouldRollback(bool auditFailure)
    {
        var failure = new FailAppealOutbox();
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext(options => options.AddInterceptors(failure));
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Private appeal text.", new string('a', 64));
        IAuditWriter writer = Substitute.For<IAuditWriter>();
        writer.Write(Arg.Any<AssetBlock.Domain.Core.Dto.Audit.AuditEvent>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("private sentinel")));
        failure.Enabled = !auditFailure;
        if (auditFailure)
        { (await CoverageStore(db, writer).RequestAppeal(request)).BlockedReasonCode.Should().Be(ErrorCodes.ERR_REVIEW_AUDIT_UNAVAILABLE); }
        else
        {
            Func<Task> operation = async () => await CoverageStore(db).RequestAppeal(request);
            await operation.Should().ThrowAsync<DbUpdateException>();
        }
        await using ApplicationDbContext observer = fixture.CreateDbContext();
        (await observer.ModerationSubmissions.AnyAsync(s => s.Kind == ModerationSubmissionKind.APPEAL)).Should().BeFalse();
        (await observer.JsonMutationIdempotencyRecords.AnyAsync(r => r.OperationId == request.OperationId)).Should().BeFalse();
        (await observer.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == workspace.Id)).CaseRevision.Should().Be(workspace.CaseRevision);
    }
    [Theory]
    [InlineData("rationale", "23514")]
    [InlineData("standard", "23514")]
    [InlineData("duplicate", "23505")]
    [InlineData("foreign", "23503")]
    [InlineData("active", "23505")]
    public async Task Appeal_WhenDatabaseConstraintIsViolated_ShouldReject(string violation, string expectedState)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        GuardedSubmissionResult result = await CoverageStore(db).RequestAppeal(request);
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == result.SubmissionId);
        Func<Task> operation = violation switch
        {
            "rationale" => async () => await db.ModerationSubmissions.Where(s => s.Id == appeal.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AppealRationale, (string?)null)),
            "standard" => async () => await db.ModerationSubmissions.Where(s => s.Id == appeal.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Kind, ModerationSubmissionKind.STANDARD)),
            "duplicate" => async () => await db.ModerationSubmissions.Where(s => s.Id == submission.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.Kind, ModerationSubmissionKind.APPEAL).SetProperty(x => x.AppealOfDecisionId, appeal.AppealOfDecisionId).SetProperty(x => x.AppealRationale, "Duplicate")),
            "active" => async () => await db.ModerationSubmissions.Where(s => s.Id == submission.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ModerationSubmissionState.SUBMITTED)),
            _ => async () => await db.ModerationSubmissions.Where(s => s.Id == appeal.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.AppealOfDecisionId, Guid.NewGuid()))
        };
        PostgresException error = (await operation.Should().ThrowAsync<PostgresException>()).Which;
        error.SqlState.Should().Be(expectedState);
    }
    [Theory]
    [InlineData(ModerationSubmissionState.SUBMITTED)]
    [InlineData(ModerationSubmissionState.IN_REVIEW)]
    [InlineData(ModerationSubmissionState.WITHDRAWN)]
    public async Task Appeal_WhenSubmissionHasNoRejection_ShouldRefuse(ModerationSubmissionState state)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, _) = await SeedClaimedDecision(db);
        if (state != ModerationSubmissionState.IN_REVIEW)
        {
            await db.ModerationSubmissions.Where(s => s.Id == submission.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, state)
                .SetProperty(x => x.ClaimHolderId, (Guid?)null).SetProperty(x => x.ClaimedAt, (DateTimeOffset?)null));
        }
        (await CoverageStore(db).GetOwnerVersionReview(submission.AssetVersionId, submission.OwnerUserId))!.CanAppeal.Should().BeFalse();
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(seed.Source.Asset.AuthorId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        (await CoverageStore(db).RequestAppeal(request)).BlockedReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_APPEAL_NOT_ALLOWED);
        (await db.ModerationSubmissions.CountAsync(s => s.Kind == ModerationSubmissionKind.APPEAL)).Should().Be(0);
    }

    [Theory]
    [InlineData(AppRoles.ADMIN)]
    [InlineData(AppRoles.USER)]
    public async Task Appeal_WhenForeignActorHasRole_ShouldDeny(string role)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        User foreign = await CoverageModerator(db, role);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(foreign.Id, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        (await CoverageStore(db).RequestAppeal(request)).BlockedReasonCode.Should().Be(ErrorCodes.ERR_FORBIDDEN);
    }
    [Fact]
    public async Task Appeal_WhenEvidenceChanges_ShouldFreezeCurrentEvidenceWithoutChangingBasis()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        SellerResponsesPageDto responses = (await CoverageStore(db).GetOwnerResponses(submission.AssetId, submission.AssetVersionId, submission.OwnerUserId, 1))!;
        (await new SaveSellerEvidenceCommandHandler(CoverageStore(db)).Handle(ResponseCommand(seed, responses, seed.Findings[0], SellerResponseType.PRIOR_WORK, "Additional evidence."), CancellationToken.None)).IsSuccess.Should().BeTrue();
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        GuardedSubmissionResult result = await CoverageStore(db).RequestAppeal(request);
        result.Status.Should().Be(GuardedSubmissionStatus.CREATED);
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == result.SubmissionId);
        appeal.SellerEvidenceRevision.Should().Be(workspace.SellerEvidenceHeadRevision).And.BeGreaterThan(submission.SellerEvidenceRevision);
        appeal.SellerEvidenceDigest.Should().NotBe(submission.SellerEvidenceDigest);
        appeal.CodeAnalysisReportHeaderId.Should().Be(submission.CodeAnalysisReportHeaderId);
    }
    [Fact]
    public async Task Appeal_WhenAnAppealIsRejected_ShouldNeverCreateAnotherAppeal()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id, workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        GuardedSubmissionResult created = await CoverageStore(db).RequestAppeal(request);
        created.Status.Should().Be(GuardedSubmissionStatus.CREATED);
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        User other = await CoverageModerator(db);
        (await CoverageStore(db).ChangeCaseClaim(new(other.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, true, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appeal.Id);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, appeal, other, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == workspace.Id);
        GuardedSubmissionResult second = await CoverageStore(db).RequestAppeal(request with { OperationId = Guid.NewGuid(), ExpectedWorkspaceRevision = workspace.WorkspaceRevision, ExpectedCaseRevision = workspace.CaseRevision });
        second.BlockedReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_APPEAL_EXISTS);
        (await db.ModerationSubmissions.CountAsync(s => s.Kind == ModerationSubmissionKind.APPEAL)).Should().Be(1);
    }
    [Fact]
    public async Task Appeal_WhenReviewerRequestsChanges_ShouldRefuseWithoutDecision()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id, workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        GuardedSubmissionResult created = await CoverageStore(db).RequestAppeal(request);
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        User other = await CoverageModerator(db);
        (await CoverageStore(db).ChangeCaseClaim(new(other.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, true, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appeal.Id);
        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, appeal, other, ModerationDecisionOutcome.REQUEST_CHANGES));
        result.ReasonCode.Should().Be("ERR_MODERATION_APPEAL_DECISION_NOT_ALLOWED");
        (await db.ModerationDecisionRecords.CountAsync(d => d.SubmissionId == appeal.Id)).Should().Be(0);
        (await db.PublicationSnapshots.AnyAsync(p => p.AssetId == appeal.AssetId)).Should().BeFalse();
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Appeal_WhenWithdrawnSubmissionFollowsRejection_ShouldStillRequireAppeal(bool changedThenReverted)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        db.ModerationSubmissions.Add(new ModerationSubmission
        {
            Id = Guid.NewGuid(),
            AssetId = submission.AssetId,
            AssetVersionId = submission.AssetVersionId,
            WorkspaceId = submission.WorkspaceId,
            WorkspaceVersionScopeKey = submission.WorkspaceVersionScopeKey,
            OwnerUserId = submission.OwnerUserId,
            ContentSha256 = changedThenReverted ? new string('c', 64) : submission.ContentSha256,
            CodeAnalysisReportHeaderId = submission.CodeAnalysisReportHeaderId,
            DeclarationRevision = submission.DeclarationRevision,
            MaterialMetadataRevision = submission.MaterialMetadataRevision,
            SellerEvidenceRevision = submission.SellerEvidenceRevision,
            SellerEvidenceDigest = submission.SellerEvidenceDigest,
            PolicyVersion = submission.PolicyVersion,
            State = ModerationSubmissionState.WITHDRAWN,
            CaseRevision = 1,
            PreviousSubmissionId = submission.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(1)
        });
        await db.SaveChangesAsync();
        SellerResponsesPageDto responses = (await CoverageStore(db).GetOwnerResponses(submission.AssetId, submission.AssetVersionId, submission.OwnerUserId, 1))!;
        Ardalis.Result.Result<Guid> result = await new SubmitAssetVersionCommandHandler(CoverageStore(db), ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, responses), CancellationToken.None);
        result.Errors.Should().Contain(ErrorCodes.ERR_MODERATION_REJECTION_FINAL);
        (await db.ModerationSubmissions.CountAsync()).Should().Be(2);
    }
    [Fact]
    public async Task Appeal_WhenUnchangedRejectionIsResubmitted_ShouldRequireAppeal()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        (await CoverageStore(db).GetOwnerVersionReview(submission.AssetVersionId, submission.OwnerUserId))!.CanAppeal.Should().BeTrue();
        SellerResponsesPageDto responses = (await CoverageStore(db).GetOwnerResponses(submission.AssetId, submission.AssetVersionId, submission.OwnerUserId, 1))!;
        Ardalis.Result.Result<Guid> result = await new SubmitAssetVersionCommandHandler(CoverageStore(db), ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, responses), CancellationToken.None);
        result.Errors.Should().Contain("ERR_MODERATION_APPEAL_REQUIRED");
        (await db.ModerationSubmissions.CountAsync()).Should().Be(1);
    }
    [Theory]
    [InlineData("content")]
    [InlineData("report")]
    [InlineData("declaration")]
    [InlineData("material")]
    [InlineData("policy")]
    public async Task Appeal_WhenRejectedBasisDiffers_ShouldAllowOrdinarySubmission(string member)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        IQueryable<ModerationSubmission> prior = db.ModerationSubmissions.Where(s => s.Id == submission.Id);
        if (member == "content")
        { await prior.ExecuteUpdateAsync(s => s.SetProperty(x => x.ContentSha256, new string('c', 64))); }
        if (member == "report")
        { await prior.ExecuteUpdateAsync(s => s.SetProperty(x => x.CodeAnalysisReportHeaderId, (Guid?)null)); }
        if (member == "declaration")
        { await prior.ExecuteUpdateAsync(s => s.SetProperty(x => x.DeclarationRevision, x => x.DeclarationRevision + 1)); }
        if (member == "material")
        { await prior.ExecuteUpdateAsync(s => s.SetProperty(x => x.MaterialMetadataRevision, x => x.MaterialMetadataRevision + 1)); }
        if (member == "policy")
        { await prior.ExecuteUpdateAsync(s => s.SetProperty(x => x.PolicyVersion, "different-policy")); }
        SellerResponsesPageDto responses = (await CoverageStore(db).GetOwnerResponses(submission.AssetId, submission.AssetVersionId, submission.OwnerUserId, 1))!;
        Ardalis.Result.Result<Guid> result = await new SubmitAssetVersionCommandHandler(CoverageStore(db), ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, responses), CancellationToken.None);
        result.IsSuccess.Should().BeTrue();
        (await db.ModerationSubmissions.CountAsync()).Should().Be(2);
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task Appeal_WhenOrdinarySubmissionRacesAppeal_ShouldCreateOnlyAppeal(int _)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var appeal = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        var ordinary = new GuardedSubmissionRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), new string('b', 64), submission.CodeAnalysisReportHeaderId);
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        await using ApplicationDbContext blocker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction held = await HoldDecisionAsset(blocker, submission.AssetId);
        await left.Database.OpenConnectionAsync();
        await right.Database.OpenConnectionAsync();
        Task<GuardedSubmissionResult> first = CoverageStore(left).RequestAppeal(appeal);
        Task<GuardedSubmissionResult> second = CoverageStore(right).AttemptGuardedSubmission(ordinary);
        await Task.Delay(200);
        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        await held.CommitAsync();
        GuardedSubmissionResult[] results = await Task.WhenAll(first, second);
        results[0].Status.Should().Be(GuardedSubmissionStatus.CREATED);
        results[1].BlockedReasonCode.Should().BeOneOf(ErrorCodes.ERR_MODERATION_APPEAL_REQUIRED, ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
        (await db.ModerationSubmissions.CountAsync()).Should().Be(2);
        (await db.ModerationSubmissions.CountAsync(s => s.State == ModerationSubmissionState.SUBMITTED)).Should().Be(1);
    }
    [Theory]
    [InlineData("open")]
    [InlineData("withdrawn")]
    [InlineData("rejected")]
    [InlineData("approved")]
    public async Task Appeal_WhenAppealExists_ShouldRefuseOrdinarySubmission(string state)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        GuardedSubmissionResult created = await CoverageStore(db).RequestAppeal(request);
        created.Status.Should().Be(GuardedSubmissionStatus.CREATED);
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        if (state == "withdrawn")
        { await db.ModerationSubmissions.Where(s => s.Id == appeal.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ModerationSubmissionState.WITHDRAWN)); }
        if (state is "rejected" or "approved")
        {
            User other = await CoverageModerator(db);
            (await CoverageStore(db).ChangeCaseClaim(new(other.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, true, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
            appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appeal.Id);
            ModerationDecisionOutcome outcome = state == "approved" ? ModerationDecisionOutcome.APPROVE : ModerationDecisionOutcome.REJECT;
            (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, appeal, other, outcome))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        }
        SellerResponsesPageDto responses = (await CoverageStore(db).GetOwnerResponses(submission.AssetId, submission.AssetVersionId, submission.OwnerUserId, 1))!;
        Ardalis.Result.Result<Guid> result = await new SubmitAssetVersionCommandHandler(CoverageStore(db), ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, responses), CancellationToken.None);
        if (state == "approved")
        { result.IsSuccess.Should().BeTrue(); }
        else
        {
            result.IsSuccess.Should().BeFalse();
            SellerVersionReviewDto review = (await CoverageStore(db).GetOwnerVersionReview(submission.AssetVersionId, submission.OwnerUserId))!;
            if (state == "open")
            { review.BlockedReasons.Should().NotContain(ErrorCodes.ERR_MODERATION_REJECTION_FINAL); }
            else
            {
                result.Errors.Should().Contain(ErrorCodes.ERR_MODERATION_REJECTION_FINAL);
                review.BlockedReasons.Should().Contain(ErrorCodes.ERR_MODERATION_REJECTION_FINAL);
            }
            (await db.ModerationSubmissions.CountAsync()).Should().Be(2);
        }
    }
    [Fact]
    public async Task Appeal_WhenPriorSubmissionIsReplayedAfterRejection_ShouldReplay()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        JsonMutationIdempotencyRecord receipt = await db.JsonMutationIdempotencyRecords.AsNoTracking().SingleAsync(r => r.ActorUserId == submission.OwnerUserId && r.OperationKind == ModerationOperationKinds.SUBMISSION_CREATE);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        var request = new GuardedSubmissionRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, submission.WorkspaceId,
            0, 0, receipt.OperationId, receipt.RequestDigest, submission.CodeAnalysisReportHeaderId);
        GuardedSubmissionResult replay = await CoverageStore(db).AttemptGuardedSubmission(request);
        replay.Status.Should().Be(GuardedSubmissionStatus.REPLAYED);
        replay.SubmissionId.Should().Be(submission.Id);
        (await db.ModerationSubmissions.CountAsync()).Should().Be(1);
    }
    [Fact]
    public async Task Appeal_WhenCorrectedSubmissionIsWithdrawnAndBasisReverted_ShouldRequireAppeal()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        var rejectedHash = new string('c', 64);
        await db.ModerationSubmissions.Where(s => s.Id == submission.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.ContentSha256, rejectedHash));
        ModerationFoundationStore store = CoverageStore(db);
        SellerResponsesPageDto responses = (await store.GetOwnerResponses(submission.AssetId, submission.AssetVersionId, submission.OwnerUserId, 1))!;
        Ardalis.Result.Result<Guid> corrected = await new SubmitAssetVersionCommandHandler(store, ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, responses), CancellationToken.None);
        corrected.IsSuccess.Should().BeTrue();
        ModerationSubmission correctedCase = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == corrected.Value);
        (await new WithdrawSubmissionCommandHandler(store).Handle(new WithdrawSubmissionCommand(submission.OwnerUserId, correctedCase.Id, correctedCase.CaseRevision, Guid.NewGuid()), CancellationToken.None)).IsSuccess.Should().BeTrue();
        await db.AssetVersions.Where(v => v.Id == submission.AssetVersionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.ContentSha256, rejectedHash));
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new GuardedSubmissionRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id,
            workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), new string('e', 64), submission.CodeAnalysisReportHeaderId);
        GuardedSubmissionResult result = await store.AttemptGuardedSubmission(request);
        result.BlockedReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_REJECTION_FINAL);
        (await db.ModerationSubmissions.CountAsync()).Should().Be(2);
    }
    [Fact]
    public async Task Appeal_WhenExclusionSetIsRead_ShouldIncludeOwnerAndPriorDeciderOnlyForAppeal()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id, workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        GuardedSubmissionResult created = await CoverageStore(db).RequestAppeal(request);
        User other = await CoverageModerator(db);
        Guid[] excluded = await AppealReviewExclusions.ForSubmission(db, created.SubmissionId!.Value).ToArrayAsync();
        excluded.Should().Contain(submission.OwnerUserId).And.Contain(reviewer.Id).And.NotContain(other.Id);
        (await AppealReviewExclusions.ForSubmission(db, submission.Id).ToArrayAsync()).Should().BeEmpty();
        (await AppealReviewExclusions.All(db).Where(x => x.SubmissionId == created.SubmissionId).Select(x => x.UserId).ToArrayAsync()).Should().BeEquivalentTo(excluded);
    }
    [Fact]
    public async Task Appeal_WhenQueueIsRead_ShouldHideCaseFromPriorDeciderAndOwner()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        User earlierReviewer = await CoverageModerator(db);
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
            ModeratorUserId = earlierReviewer.Id,
            Outcome = ModerationSubmissionState.REJECTED,
            CaseRevision = 1,
            Message = "Earlier decision.",
            CreatedAt = earlier.CreatedAt
        });
        await db.SaveChangesAsync();
        Guid previousId = earlier.Id;
        for (var i = 0; i < 18; i++)
        {
            var predecessor = new ModerationSubmission
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
                State = ModerationSubmissionState.WITHDRAWN,
                CaseRevision = 1,
                PreviousSubmissionId = previousId,
                CreatedAt = submission.CreatedAt.AddHours(-18 + i)
            };
            db.ModerationSubmissions.Add(predecessor);
            await db.SaveChangesAsync();
            previousId = predecessor.Id;
        }
        await db.ModerationSubmissions.Where(s => s.Id == submission.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.PreviousSubmissionId, previousId));
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        var request = new AppealCreateRequest(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id, workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64));
        GuardedSubmissionResult created = await CoverageStore(db).RequestAppeal(request);
        await db.Users.Where(u => u.Id == submission.OwnerUserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.MODERATOR));
        User other = await CoverageModerator(db);
        var store = new ModerationFoundationStore(db, new AssetBlock.Infrastructure.Persistence.Stores.AssetStore(db), new AssetBlock.Infrastructure.Persistence.Stores.UserStore(db), new EfUnitOfWork(db), dataProtectionProvider: new EphemeralDataProtectionProvider());
        (await store.GetModeratorQueue(new(reviewer.Id, null, CaseClaimFilter.ALL, 50, null))).Value!.Items.Select(x => x.SubmissionId).Should().NotContain(created.SubmissionId!.Value);
        (await store.GetModeratorQueue(new(earlierReviewer.Id, null, CaseClaimFilter.ALL, 50, null))).Value!.Items.Select(x => x.SubmissionId).Should().NotContain(created.SubmissionId!.Value);
        (await store.GetModeratorQueue(new(submission.OwnerUserId, null, CaseClaimFilter.ALL, 50, null))).Value!.Items.Select(x => x.SubmissionId).Should().NotContain(created.SubmissionId!.Value);
        (await store.GetModeratorQueue(new(other.Id, null, CaseClaimFilter.ALL, 50, null))).Value!.Items.Select(x => x.SubmissionId).Should().Contain(created.SubmissionId!.Value);
        async Task<Guid> AddQueueCase(int number)
        {
            AssetVersion version = TestData.CreateAssetVersion(submission.AssetId, versionNumber: number, isCurrent: false, processingStatus: AssetVersionProcessingStatus.READY);
            var extraWorkspace = new AssetDraftWorkspace { Id = Guid.NewGuid(), AssetId = submission.AssetId, AssetVersionId = version.Id, WorkspaceVersionScopeKey = version.Id, ScopeId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
            var extra = new ModerationSubmission { Id = Guid.NewGuid(), AssetId = submission.AssetId, AssetVersionId = version.Id, WorkspaceId = extraWorkspace.Id, WorkspaceVersionScopeKey = version.Id, OwnerUserId = submission.OwnerUserId, ContentSha256 = version.ContentSha256, DeclarationRevision = 1, MaterialMetadataRevision = 1, SellerEvidenceRevision = 0, SellerEvidenceDigest = new string('e', 64), PolicyVersion = ReviewAnalysisPolicy.KEY, State = ModerationSubmissionState.SUBMITTED, CaseRevision = 1, CreatedAt = DateTimeOffset.UtcNow };
            db.AssetVersions.Add(version);
            db.AssetDraftWorkspaces.Add(extraWorkspace);
            db.ModerationSubmissions.Add(extra);
            await db.SaveChangesAsync();
            return extra.Id;
        }
        Guid firstId = await AddQueueCase(2);
        Guid lastId = await AddQueueCase(3);
        DateTimeOffset midpoint = DateTimeOffset.UtcNow;
        await db.ModerationSubmissions.Where(s => s.Id == firstId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, midpoint.AddMinutes(-1)));
        await db.ModerationSubmissions.Where(s => s.Id == created.SubmissionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, midpoint));
        await db.ModerationSubmissions.Where(s => s.Id == lastId).ExecuteUpdateAsync(s => s.SetProperty(x => x.CreatedAt, midpoint.AddMinutes(1)));
        ReviewAccessResult<ModeratorQueuePage> page1 = await store.GetModeratorQueue(new(reviewer.Id, null, CaseClaimFilter.ALL, 1, null));
        ReviewAccessResult<ModeratorQueuePage> page2 = await store.GetModeratorQueue(new(reviewer.Id, null, CaseClaimFilter.ALL, 1, page1.Value!.NextCursor));
        page1.Value!.Items.Select(x => x.SubmissionId).Should().Equal(firstId);
        page2.Value!.Items.Select(x => x.SubmissionId).Should().Equal(lastId);
        page2.Value.NextCursor.Should().BeNull();
        ReviewAccessResult<ModeratorQueuePage> other1 = await store.GetModeratorQueue(new(other.Id, null, CaseClaimFilter.ALL, 1, null));
        ReviewAccessResult<ModeratorQueuePage> other2 = await store.GetModeratorQueue(new(other.Id, null, CaseClaimFilter.ALL, 1, other1.Value!.NextCursor));
        ReviewAccessResult<ModeratorQueuePage> other3 = await store.GetModeratorQueue(new(other.Id, null, CaseClaimFilter.ALL, 1, other2.Value!.NextCursor));
        other1.Value!.Items.Select(x => x.SubmissionId).Should().Equal(firstId);
        other2.Value!.Items.Select(x => x.SubmissionId).Should().Equal(created.SubmissionId!.Value);
        other3.Value!.Items.Select(x => x.SubmissionId).Should().Equal(lastId);
        other3.Value.NextCursor.Should().BeNull();
        await db.ModerationSubmissions.Where(s => s.Id == created.SubmissionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.PreviousSubmissionId, earlier.Id));
        (await AppealReviewExclusions.ForSubmission(db, created.SubmissionId!.Value).ToArrayAsync()).Should().Contain(other.Id);
        (await store.GetModeratorQueue(new(other.Id, null, CaseClaimFilter.ALL, 50, null))).Value!.Items.Select(x => x.SubmissionId).Should().NotContain(created.SubmissionId.Value);
        Guid[] broken = await AppealReviewExclusions.ForSubmission(db, created.SubmissionId.Value).ToArrayAsync();
        (await AppealReviewExclusions.All(db).Where(x => x.SubmissionId == created.SubmissionId).Select(x => x.UserId).ToArrayAsync()).Should().BeEquivalentTo(broken);
        await db.ModerationSubmissions.Where(s => s.Id == created.SubmissionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.PreviousSubmissionId, submission.Id));
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        (await CoverageStore(db).ChangeCaseClaim(new(other.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, true, new string('f', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, appeal, other, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
    }
    [Fact]
    public async Task Appeal_WhenPriorDeciderClaimsOrReads_ShouldDenyWithoutClaim()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        GuardedSubmissionResult created = await CoverageStore(db).RequestAppeal(new(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id, workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Private rationale.", new string('a', 64)));
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        var historyCount = await db.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == appeal.Id);
        CaseClaimResult claim = await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, true, new string('b', 64)));
        claim.Status.ToString().Should().Be("APPEAL_REVIEWER_EXCLUDED");
        await db.Users.Where(u => u.Id == submission.OwnerUserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.MODERATOR));
        (await CoverageStore(db).ChangeCaseClaim(new(submission.OwnerUserId, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, true, new string('c', 64)))).Status.ToString().Should().Be("APPEAL_REVIEWER_EXCLUDED");
        (await CoverageStore(db).GetCaseSummaryForModerator(submission.OwnerUserId, appeal.Id)).Status.ToString().Should().Be("APPEAL_REVIEWER_EXCLUDED");
        (await CoverageStore(db).ReadRestrictedEvidence(submission.OwnerUserId, appeal.Id, false, RestrictedEvidenceKind.OWN_EXCERPT, seed.Findings[0])).ReasonCode.Should().Be("ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED");
        (await CoverageStore(db).GetCaseSummaryForModerator(reviewer.Id, appeal.Id)).Status.ToString().Should().Be("APPEAL_REVIEWER_EXCLUDED");
        ReviewAccessResult<RestrictedEvidenceDto> evidence = await CoverageStore(db).ReadRestrictedEvidence(reviewer.Id, appeal.Id, false, RestrictedEvidenceKind.OWN_EXCERPT, seed.Findings[0]);
        evidence.ReasonCode.Should().Be("ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED");
        ModerationSubmission after = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appeal.Id);
        after.State.Should().Be(ModerationSubmissionState.SUBMITTED);
        after.ClaimHolderId.Should().BeNull();
        (await db.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == appeal.Id)).Should().Be(historyCount);
        (await db.AuditLogs.AsNoTracking().Where(a => a.ResourceId == appeal.Id.ToString()).Select(a => a.MetadataJson).ToListAsync()).Should().OnlyContain(x => x == null || !x.Contains("Private rationale."));
    }
    [Fact]
    public async Task Appeal_WhenExcludedHolderDecides_ShouldDenyButAllowRelease()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        GuardedSubmissionResult created = await CoverageStore(db).RequestAppeal(new(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id, workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Private rationale.", new string('a', 64)));
        await db.ModerationSubmissions.Where(s => s.Id == created.SubmissionId).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, ModerationSubmissionState.IN_REVIEW)
            .SetProperty(x => x.ClaimHolderId, reviewer.Id).SetProperty(x => x.ClaimedAt, DateTimeOffset.UtcNow));
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        User other = await CoverageModerator(db);
        var queue = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db), dataProtectionProvider: new EphemeralDataProtectionProvider());
        ModeratorQueueItem item = (await queue.GetModeratorQueue(new(other.Id, null, CaseClaimFilter.UNCLAIMED, 50, null))).Value!.Items.Single(i => i.SubmissionId == appeal.Id);
        item.Kind.Should().Be(ModerationSubmissionKind.APPEAL);
        item.State.Should().Be(ModerationSubmissionState.SUBMITTED);
        item.ClaimHolderId.Should().BeNull();
        ModerationCaseAccessResult caseAccess = await CoverageStore(db).GetCaseSummaryForModerator(other.Id, appeal.Id);
        caseAccess.Status.Should().Be(ModerationCaseAccessStatus.FOUND);
        caseAccess.Summary!.Kind.Should().Be(ModerationSubmissionKind.APPEAL);
        ModerationDecisionResult denied = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, appeal, reviewer, ModerationDecisionOutcome.APPROVE));
        denied.Status.Should().Be(ModerationDecisionStatus.FORBIDDEN);
        denied.ReasonCode.Should().Be("ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED");
        (await db.ModerationDecisionRecords.AnyAsync(d => d.SubmissionId == appeal.Id)).Should().BeFalse();
        (await db.PublicationSnapshots.AnyAsync(p => p.AssetId == appeal.AssetId)).Should().BeFalse();
        (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == appeal.AssetId)).CurrentPublicationSnapshotId.Should().BeNull();
        (await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, false, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appeal.Id);
        (await CoverageStore(db).ChangeCaseClaim(new(other.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, true, new string('c', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appeal.Id);
        await db.Assets.Where(a => a.Id == appeal.AssetId).ExecuteUpdateAsync(s => s.SetProperty(a => a.AuthorId, other.Id));
        var ownerRelease = new CaseClaimChange(other.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, false, new string('d', 64));
        (await CoverageStore(db).ChangeCaseClaim(ownerRelease)).Status.Should().Be(CaseClaimStatus.SUCCESS);
        CaseClaimResult ownerReplay = await CoverageStore(db).ChangeCaseClaim(ownerRelease);
        ownerReplay.Status.Should().Be(CaseClaimStatus.SUCCESS);
        ownerReplay.Value!.Replayed.Should().BeTrue();
    }
    [Fact]
    public async Task Appeal_WhenOnlyOriginalReviewerExists_ShouldRemainPending()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        GuardedSubmissionResult created = await CoverageStore(db).RequestAppeal(new(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id, workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Private rationale.", new string('a', 64)));
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        var histories = await db.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == appeal.Id);
        TimeProvider clock = Substitute.For<TimeProvider>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        clock.GetUtcNow().Returns(now);
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db), timeProvider: clock, dataProtectionProvider: new EphemeralDataProtectionProvider());
        for (var i = 0; i < 2; i++)
        {
            clock.GetUtcNow().Returns(now.AddDays(i * 30));
            (await store.GetModeratorQueue(new(reviewer.Id, null, CaseClaimFilter.ALL, 50, null))).Value!.Items.Should().BeEmpty();
            (await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, true, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.APPEAL_REVIEWER_EXCLUDED);
        }
        ModerationSubmission after = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appeal.Id);
        after.State.Should().Be(ModerationSubmissionState.SUBMITTED);
        after.ClaimHolderId.Should().BeNull();
        after.CaseRevision.Should().Be(appeal.CaseRevision);
        (await db.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == appeal.Id)).Should().Be(histories);
        (await db.ModerationDecisionRecords.AnyAsync(d => d.SubmissionId == appeal.Id)).Should().BeFalse();
    }
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(10)]
    public async Task Appeal_WhenReviewerRoleIsRevokedUnderLock_ShouldDenyDecision(int _)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User priorReviewer) = await SeedClaimedDecision(db);
        (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, priorReviewer, ModerationDecisionOutcome.REJECT))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == submission.WorkspaceId);
        GuardedSubmissionResult created = await CoverageStore(db).RequestAppeal(new(submission.OwnerUserId, submission.AssetId, submission.AssetVersionId, workspace.Id, workspace.WorkspaceRevision, workspace.CaseRevision, Guid.NewGuid(), "Review again.", new string('a', 64)));
        ModerationSubmission appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == created.SubmissionId);
        User reviewer = await CoverageModerator(db);
        (await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, appeal.Id, Guid.NewGuid(), appeal.CaseRevision, true, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        appeal = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == appeal.Id);
        await using ApplicationDbContext revoker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction held = await revoker.Database.BeginTransactionAsync();
        await revoker.Users.Where(u => u.Id == reviewer.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        await using ApplicationDbContext decider = fixture.CreateDbContext();
        await decider.Database.OpenConnectionAsync();
        Task<ModerationDecisionResult> pending = CoverageStore(decider).DecideModerationCase(DecisionRequest(seed, appeal, reviewer, ModerationDecisionOutcome.APPROVE));
        await Task.Delay(200);
        pending.IsCompleted.Should().BeFalse();
        await held.CommitAsync();
        (await pending.WaitAsync(TimeSpan.FromSeconds(15))).Status.Should().Be(ModerationDecisionStatus.FORBIDDEN);
        (await db.ModerationDecisionRecords.AnyAsync(d => d.SubmissionId == appeal.Id)).Should().BeFalse();
        (await db.PublicationSnapshots.AnyAsync(p => p.AssetId == appeal.AssetId)).Should().BeFalse();
    }
}
