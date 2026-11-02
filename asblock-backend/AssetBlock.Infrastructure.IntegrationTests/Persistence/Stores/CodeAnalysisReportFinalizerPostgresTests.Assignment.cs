using System.Data.Common;
using System.Text.Json;
using AssetBlock.Application.UseCases.Assets.SaveSellerEvidence;
using AssetBlock.Application.UseCases.Assets.SubmitAssetVersion;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

public sealed partial class CodeAnalysisReportFinalizerPostgresTests
{
    private sealed class FailLifecycleWrite : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && data.Context!.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added && e.Entity.Type == OutboxMessageTypes.MODERATION_LIFECYCLE))
            { throw new DbUpdateException("Lifecycle event write failed."); }
            return base.SavingChangesAsync(data, result, cancellationToken);
        }
    }

    private sealed class LifecycleCommandCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Count); return base.ReaderExecutingAsync(command, data, result, cancellationToken); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Count); return base.NonQueryExecutingAsync(command, data, result, cancellationToken); }
    }

    private sealed class FailSecondFanoutBatch : DbCommandInterceptor
    {
        private int _inserts;
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("INSERT INTO outbox_messages", StringComparison.Ordinal) && Interlocked.Increment(ref _inserts) == 2)
            { throw new DbUpdateException("Notification batch failed."); }
            return base.NonQueryExecutingAsync(command, data, result, cancellationToken);
        }
    }

    [Fact]
    public async Task Assignment_WhenFanoutBatchFails_ShouldResumeWithoutDuplicateNotifications()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, _) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        OutboxMessage message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE);
        await using (ApplicationDbContext failing = fixture.CreateDbContext(options => options.AddInterceptors(new FailSecondFanoutBatch())))
        {
            Func<Task> operation = () => new ModerationLifecycleOutboxHandler(failing, TimeProvider.System).Handle(message, CancellationToken.None);
            await operation.Should().ThrowAsync<DbUpdateException>();
        }
        (await db.UserNotifications.CountAsync(n => n.Kind == NotificationKind.MODERATION_SUBMITTED)).Should().Be(1);
        await new ModerationLifecycleOutboxHandler(db, TimeProvider.System).Handle(message, CancellationToken.None);
        List<Guid> recipients = await db.UserNotifications.Where(n => n.Kind == NotificationKind.MODERATION_SUBMITTED).Select(n => n.RecipientUserId).ToListAsync();
        recipients.Should().BeEquivalentTo([seed.Source.Asset.AuthorId, reviewer.Id]);
    }

    [Fact]
    public async Task Assignment_WhenQueueFanoutExceedsOnePage_ShouldBatchWritesWithoutSkipping()
    {
        var counter = new LifecycleCommandCounter();
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext(options => options.AddInterceptors(counter));
        (_, Guid submissionId) = await SeedSubmittedCase(db);
        for (var i = 0; i < 101; i++)
        {
            User reviewer = TestData.CreateUser($"reviewer-{i}", $"reviewer-{i}@example.test");
            reviewer.Role = AppRoles.MODERATOR;
            db.Users.Add(reviewer);
        }
        await db.SaveChangesAsync();
        OutboxMessage message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE);
        counter.Count = 0;
        await new ModerationLifecycleOutboxHandler(db, TimeProvider.System).Handle(message, CancellationToken.None);
        counter.Count.Should().BeLessThan(20);
        (await db.UserNotifications.CountAsync(n => n.Kind == NotificationKind.MODERATION_SUBMITTED)).Should().Be(102);
        (await db.ModerationSubmissions.AnyAsync(s => s.Id == submissionId)).Should().BeTrue();
    }

    [Fact]
    public async Task Assignment_WhenNotificationWriteFails_ShouldRollbackClaim()
    {
        var failure = new FailLifecycleWrite();
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext(options => options.AddInterceptors(failure));
        (_, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        ModerationSubmission submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        failure.Enabled = true;
        Func<Task> operation = async () => await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), submission.CaseRevision, true, new string('a', 64)));
        await operation.Should().ThrowAsync<DbUpdateException>();
        await using ApplicationDbContext observer = fixture.CreateDbContext();
        (await observer.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId)).State.Should().Be(ModerationSubmissionState.SUBMITTED);
        (await observer.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE)).Should().Be(1);
        (await observer.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == submissionId && h.State == ModerationSubmissionState.IN_REVIEW)).Should().Be(0);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Assignment_WhenReleasedOrSuperseded_ShouldNotInventNotification(bool release)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        ModerationSubmission submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        (await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), submission.CaseRevision, true, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        if (release)
        { (await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), submission.CaseRevision, false, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS); }
        else
        {
            SellerResponsesPageDto current = (await CoverageStore(db).GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
            var edit = new SaveSellerEvidenceCommand(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, current.WorkspaceId,
                seed.ReportId, null, null, "Changed note", current.WorkspaceRevision, current.EvidenceRevision, Guid.NewGuid());
            (await new SaveSellerEvidenceCommandHandler(CoverageStore(db)).Handle(edit, CancellationToken.None)).IsSuccess.Should().BeTrue();
        }
        (await db.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE)).Should().Be(2);
    }
    [Fact]
    public async Task Assignment_WhenTransitionsReplay_ShouldPersistOneSafeEventPerTransition()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        ModerationSubmission submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        var claim = new CaseClaimChange(reviewer.Id, submissionId, Guid.NewGuid(), submission.CaseRevision, true, new string('a', 64));
        (await CoverageStore(db).ChangeCaseClaim(claim)).Status.Should().Be(CaseClaimStatus.SUCCESS);
        (await CoverageStore(db).ChangeCaseClaim(claim)).Value!.Replayed.Should().BeTrue();
        submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        var withdraw = new WithdrawSubmissionRequest(seed.Source.Asset.AuthorId, submissionId, submission.CaseRevision, Guid.NewGuid(), new string('b', 64), ModerationWithdrawalReason.SELLER_WITHDRAWAL);
        (await CoverageStore(db).WithdrawSubmission(withdraw)).Status.Should().Be(WithdrawSubmissionStatus.SUCCEEDED);
        (await CoverageStore(db).WithdrawSubmission(withdraw)).Replayed.Should().BeTrue();
        List<OutboxMessage> events = await db.OutboxMessages.AsNoTracking().Where(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE).ToListAsync();
        events.Should().HaveCount(3);
        events.Select(e => e.Id).Distinct().Should().HaveCount(3);
        Guid firstEventId = events[0].Id;
        Func<Task> duplicate = () => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO outbox_messages ("Id", "Type", "Payload", "OccurredAt", "Status", "AttemptCount", "ReplayCount")
            SELECT "Id", "Type", "Payload", "OccurredAt", "Status", "AttemptCount", "ReplayCount" FROM outbox_messages WHERE "Id" = {firstEventId}
            """);
        (await duplicate.Should().ThrowAsync<Npgsql.PostgresException>()).Which.SqlState.Should().Be(Npgsql.PostgresErrorCodes.UniqueViolation);
        events.Select(e => e.Payload).Should().OnlyContain(p => !p.Contains("Private", StringComparison.OrdinalIgnoreCase) && !p.Contains("Original implementation", StringComparison.Ordinal));
        OutboxMessage withdrawnEvent = events.Single(e => e.Id == ModerationEventIdentity.Event(submissionId, submission.CaseRevision + 1, NotificationKind.MODERATION_WITHDRAWN, false));
        var handler = new ModerationLifecycleOutboxHandler(db, TimeProvider.System);
        await handler.Handle(withdrawnEvent, CancellationToken.None);
        await handler.Handle(withdrawnEvent, CancellationToken.None);
        List<UserNotification> notices = await db.UserNotifications.AsNoTracking().Where(n => n.Kind == NotificationKind.MODERATION_WITHDRAWN).ToListAsync();
        notices.Select(n => n.RecipientUserId).Should().BeEquivalentTo([seed.Source.Asset.AuthorId, reviewer.Id]);
        notices.Select(n => n.MetadataJson).Should().OnlyContain(json => !json.Contains(reviewer.Id.ToString(), StringComparison.Ordinal)
            && !json.Contains("Original implementation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Assignment_WhenLifecycleEventReplayed_ShouldPersistOneNotificationPerRecipient()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db);
        User moderator = await CoverageModerator(db);
        User other = await CoverageModerator(db, AppRoles.USER);
        User admin = await CoverageModerator(db, AppRoles.ADMIN);
        await db.Users.Where(u => u.Id == seed.Source.Asset.AuthorId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.MODERATOR));
        OutboxMessage message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE);
        await using ApplicationDbContext first = fixture.CreateDbContext();
        await using ApplicationDbContext second = fixture.CreateDbContext();
        await Task.WhenAll(new ModerationLifecycleOutboxHandler(first, TimeProvider.System).Handle(message, CancellationToken.None),
            new ModerationLifecycleOutboxHandler(second, TimeProvider.System).Handle(message, CancellationToken.None));
        await new ModerationLifecycleOutboxHandler(db, TimeProvider.System).Handle(message, CancellationToken.None);
        List<UserNotification> rows = await db.UserNotifications.AsNoTracking().Where(n => n.Kind == NotificationKind.MODERATION_SUBMITTED).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(n => n.RecipientUserId).Should().BeEquivalentTo([seed.Source.Asset.AuthorId, moderator.Id]);
        rows.Should().OnlyContain(n => n.Kind == NotificationKind.MODERATION_SUBMITTED && !n.MetadataJson.Contains("Original implementation", StringComparison.Ordinal));
        rows.Select(n => n.MetadataJson).Should().OnlyContain(json => !json.Contains(moderator.Id.ToString(), StringComparison.Ordinal)
            && !json.Contains(new string('b', 64), StringComparison.Ordinal) && !json.Contains("evidence", StringComparison.OrdinalIgnoreCase));
        (await db.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.NOTIFICATION_DISPATCH && (m.Id == rows[0].Id || m.Id == rows[1].Id))).Should().Be(2);
        var store = new NotificationStore(db, NullLogger<NotificationStore>.Instance);
        (await store.GetPaged(other.Id, new GetNotificationsRequest())).Items.Should().BeEmpty();
        (await store.GetPaged(admin.Id, new GetNotificationsRequest())).Items.Should().BeEmpty();
        (await store.GetPaged(seed.Source.Asset.AuthorId, new GetNotificationsRequest())).Items.Should().ContainSingle(n => n.Kind == NotificationKind.MODERATION_SUBMITTED);
        (await store.MarkRead(other.Id, rows.Single(n => n.RecipientUserId == seed.Source.Asset.AuthorId).Id)).Should().BeFalse();
    }

    [Fact]
    public async Task Assignment_WhenOwnerChangesBeforeFanout_ShouldExcludeBothOwnersFromModeratorAudience()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, _) = await SeedSubmittedCase(db);
        User newOwner = await CoverageModerator(db);
        User moderator = await CoverageModerator(db);
        await db.Users.Where(u => u.Id == seed.Source.Asset.AuthorId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.MODERATOR));
        await db.Assets.Where(a => a.Id == seed.Source.Asset.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.AuthorId, newOwner.Id));
        OutboxMessage message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE);
        await new ModerationLifecycleOutboxHandler(db, TimeProvider.System).Handle(message, CancellationToken.None);
        List<Guid> recipients = await db.UserNotifications.AsNoTracking().Where(n => n.Kind == NotificationKind.MODERATION_SUBMITTED).Select(n => n.RecipientUserId).ToListAsync();
        recipients.Should().BeEquivalentTo([newOwner.Id, moderator.Id]);
    }

    [Fact]
    public async Task Lifecycle_WhenPrivateTextIsSubmittedAndApproved_ShouldKeepItOutOfDurableRows()
    {
        const string responseText = "SELLER-PRIVATE-SENTINEL-4f2a";
        const string rationale = "MODERATOR-RATIONALE-SENTINEL-9c1d";
        const string dispositionText = "MODERATOR-DISPOSITION-SENTINEL-7b3e";
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        ModerationFoundationStore store = CoverageStore(db);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        (await new SaveSellerEvidenceCommandHandler(store).Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.INDEPENDENT_IMPLEMENTATION, responseText), CancellationToken.None)).IsSuccess.Should().BeTrue();
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Guid submissionId = (await new SubmitAssetVersionCommandHandler(store, ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, current), CancellationToken.None)).Value;
        User reviewer = await CoverageModerator(db);
        ModerationSubmission submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        (await store.ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), submission.CaseRevision, true, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        ModerationDecisionRequest decision = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE) with
        {
            Rationale = rationale,
            Dispositions = [new(seed.Findings[0], FindingDispositionType.EXPLAINED, dispositionText)]
        };
        (await CoverageStore(db).DecideModerationCase(decision)).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);

        var lifecycleHandler = new ModerationLifecycleOutboxHandler(db, TimeProvider.System);
        foreach (OutboxMessage lifecycle in await db.OutboxMessages.AsNoTracking().Where(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE).ToListAsync())
        { await lifecycleHandler.Handle(lifecycle, CancellationToken.None); }
        OutboxMessage decided = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == OutboxMessageTypes.MODERATION_DECISION);
        await new ModerationDecisionOutboxHandler(Substitute.For<IRealtimeNotificationPublisher>(), Substitute.For<ICacheService>(),
            Substitute.For<IPublicationEmbeddingEnqueuer>(), new AssetStore(db)).Handle(decided, CancellationToken.None);

        CodeAnalysisReportFinding finding = await db.CodeAnalysisReportFindings.AsNoTracking().SingleAsync(f => f.Id == seed.Findings[0]);
        var durable = new List<string>();
        durable.AddRange(await db.OutboxMessages.AsNoTracking().Select(m => m.Payload).ToListAsync());
        durable.AddRange(await db.UserNotifications.AsNoTracking().Select(n => n.MetadataJson).ToListAsync());
        durable.AddRange((await db.AuditLogs.AsNoTracking().Select(a => a.MetadataJson).ToListAsync()).OfType<string>());
        durable.AddRange(await db.ModerationSubmissionHistoryEntries.AsNoTracking().Select(h => h.Summary).ToListAsync());
        durable.Should().HaveCountGreaterThan(3);
        var privateTexts = new List<string> { responseText, rationale, dispositionText };
        if (finding.OwnExcerpt.Length > 0)
        { privateTexts.Add(finding.OwnExcerpt); }
        if (!string.IsNullOrEmpty(finding.ForeignSourceId))
        { privateTexts.Add(finding.ForeignSourceId); }
        durable.Should().NotContain(text => privateTexts.Any(secret => text.Contains(secret, StringComparison.Ordinal)));
        (await db.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE)).Should().Be(2);
        (await db.UserNotifications.CountAsync(n => n.Kind == NotificationKind.MODERATION_SUBMITTED)).Should().Be(2);
    }

    private sealed class QueueCommandCounter : DbCommandInterceptor
    {
        public int Count;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Count);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    private static async Task<(ResponseReviewSeed Seed, Guid SubmissionId)> SeedSubmittedCase(ApplicationDbContext db, string[]? tags = null)
    {
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1, tags);
        ModerationFoundationStore store = CoverageStore(db);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        (await new SaveSellerEvidenceCommandHandler(store).Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.NO_EVIDENCE, ""), CancellationToken.None)).IsSuccess.Should().BeTrue();
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Ardalis.Result.Result<Guid> submitted = await new SubmitAssetVersionCommandHandler(store, ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        submitted.IsSuccess.Should().BeTrue();
        return (seed, submitted.Value);
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
    public async Task Assignment_WhenTwoModeratorsRace_ShouldAllowOnlyOneAndReleaseWithReplay(int _)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db);
        User first = await CoverageModerator(db);
        User second = await CoverageModerator(db);
        var revision = await db.ModerationSubmissions.Where(s => s.Id == submissionId).Select(s => s.CaseRevision).SingleAsync();
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        await left.Database.OpenConnectionAsync();
        await right.Database.OpenConnectionAsync();
        var one = new CaseClaimChange(first.Id, submissionId, Guid.NewGuid(), revision, true, new string('a', 64));
        var two = new CaseClaimChange(second.Id, submissionId, Guid.NewGuid(), revision, true, new string('b', 64));
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CaseClaimResult> ClaimAfterBarrier(ApplicationDbContext context, CaseClaimChange input)
        { await start.Task; return await CoverageStore(context).ChangeCaseClaim(input); }
        Task<CaseClaimResult> leftClaim = ClaimAfterBarrier(left, one);
        Task<CaseClaimResult> rightClaim = ClaimAfterBarrier(right, two);
        start.SetResult();
        CaseClaimResult[] results = await Task.WhenAll(leftClaim, rightClaim);
        results.Count(r => r.Status == CaseClaimStatus.SUCCESS).Should().Be(1);
        results.Count(r => r.Status == CaseClaimStatus.HELD).Should().Be(1);
        CaseClaimChange winner = results[0].Status == CaseClaimStatus.SUCCESS ? one : two;
        CaseClaimChange loser = winner == one ? two : one;
        (await CoverageStore(db).ChangeCaseClaim(loser with { OperationId = Guid.NewGuid(), Claim = false, ExpectedCaseRevision = revision + 1 })).Status.Should().Be(CaseClaimStatus.NOT_HOLDER);
        (await CoverageStore(db).ChangeCaseClaim(winner)).Value!.Replayed.Should().BeTrue();
        (await CoverageStore(db).ChangeCaseClaim(winner with { Digest = new string('c', 64) })).Status.Should().Be(CaseClaimStatus.OPERATION_CONFLICT);
        CaseClaimChange repeated = winner with { OperationId = Guid.NewGuid() };
        CaseClaimResult sameHolder = await CoverageStore(db).ChangeCaseClaim(repeated);
        sameHolder.Status.Should().Be(CaseClaimStatus.SUCCESS);
        sameHolder.Value!.CaseRevision.Should().Be(revision + 1);
        (await CoverageStore(db).ChangeCaseClaim(repeated)).Value!.Replayed.Should().BeTrue();
        var release = new CaseClaimChange(winner.ActorId, submissionId, Guid.NewGuid(), revision + 1, false, new string('d', 64));
        (await CoverageStore(db).ChangeCaseClaim(release)).Status.Should().Be(CaseClaimStatus.SUCCESS);
        (await CoverageStore(db).ChangeCaseClaim(release)).Value!.Replayed.Should().BeTrue();
        (await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId)).State.Should().Be(ModerationSubmissionState.SUBMITTED);
        List<AuditLog> audits = await db.AuditLogs.AsNoTracking().Where(a => a.ResourceId == submissionId.ToString() && (a.Action == AuditActions.CASE_CLAIMED || a.Action == AuditActions.CASE_RELEASED)).ToListAsync();
        audits.Select(a => a.Action).Should().BeEquivalentTo(new[] { AuditActions.CASE_CLAIMED, AuditActions.CASE_RELEASED });
        audits.Select(a => a.MetadataJson).Should().OnlyContain(json => json != null && !json.Contains("private", StringComparison.OrdinalIgnoreCase));
        List<string> history = await db.ModerationSubmissionHistoryEntries.AsNoTracking().Where(h => h.SubmissionId == submissionId).OrderBy(h => h.CaseRevision).Select(h => h.Summary).ToListAsync();
        history.Should().Contain("Claimed for review.").And.Contain("Released from review.");
        (await db.PublicationSnapshots.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Assignment_WhenHolderRevoked_ShouldAllowTakeover()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (_, Guid submissionId) = await SeedSubmittedCase(db);
        User first = await CoverageModerator(db);
        User second = await CoverageModerator(db);
        var revision = await db.ModerationSubmissions.Where(s => s.Id == submissionId).Select(s => s.CaseRevision).SingleAsync();
        (await CoverageStore(db).ChangeCaseClaim(new(first.Id, submissionId, Guid.NewGuid(), revision, true, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        await db.Users.Where(u => u.Id == first.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db), dataProtectionProvider: new EphemeralDataProtectionProvider());
        ReviewAccessResult<ModeratorQueuePage> queued = await store.GetModeratorQueue(new(second.Id, ModerationSubmissionState.SUBMITTED, CaseClaimFilter.UNCLAIMED, 50, null));
        queued.Value!.Items.Should().ContainSingle(i => i.SubmissionId == submissionId && i.State == ModerationSubmissionState.SUBMITTED && i.ClaimHolderId == null);
        (await CoverageStore(db).ChangeCaseClaim(new(second.Id, submissionId, Guid.NewGuid(), revision + 1, true, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        (await db.ModerationSubmissionHistoryEntries.AsNoTracking().Where(h => h.SubmissionId == submissionId).OrderBy(h => h.CaseRevision).Select(h => h.Summary).LastAsync()).Should().Contain("taken over");
        AuditLog audit = await db.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.CASE_CLAIMED && a.ResourceId == submissionId.ToString()).OrderByDescending(a => a.Id).FirstAsync();
        using var metadata = JsonDocument.Parse(audit.MetadataJson!);
        metadata.RootElement.GetProperty("previousHolderId").GetGuid().Should().Be(first.Id);
    }

    [Fact]
    public async Task Assignment_WhenTwoModeratorsRaceToTakeOver_ShouldAllowOne()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (_, Guid submissionId) = await SeedSubmittedCase(db);
        User old = await CoverageModerator(db);
        User first = await CoverageModerator(db);
        User second = await CoverageModerator(db);
        var revision = await db.ModerationSubmissions.Where(s => s.Id == submissionId).Select(s => s.CaseRevision).SingleAsync();
        (await CoverageStore(db).ChangeCaseClaim(new(old.Id, submissionId, Guid.NewGuid(), revision, true, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        await db.Users.Where(u => u.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        await left.Database.OpenConnectionAsync();
        await right.Database.OpenConnectionAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CaseClaimResult> Run(ApplicationDbContext context, Guid actor)
        { await start.Task; return await CoverageStore(context).ChangeCaseClaim(new(actor, submissionId, Guid.NewGuid(), revision + 1, true, new string('b', 64))); }
        Task<CaseClaimResult> a = Run(left, first.Id);
        Task<CaseClaimResult> b = Run(right, second.Id);
        start.SetResult();
        CaseClaimResult[] results = await Task.WhenAll(a, b);
        results.Count(r => r.Status == CaseClaimStatus.SUCCESS).Should().Be(1);
        results.Count(r => r.Status == CaseClaimStatus.HELD).Should().Be(1);
    }

    [Fact]
    public async Task Assignment_WhenReleaseRacesClaim_ShouldSerialize()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (_, Guid submissionId) = await SeedSubmittedCase(db);
        User holder = await CoverageModerator(db);
        User other = await CoverageModerator(db);
        var revision = await db.ModerationSubmissions.Where(s => s.Id == submissionId).Select(s => s.CaseRevision).SingleAsync();
        (await CoverageStore(db).ChangeCaseClaim(new(holder.Id, submissionId, Guid.NewGuid(), revision, true, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        await left.Database.OpenConnectionAsync();
        await right.Database.OpenConnectionAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CaseClaimResult> Run(ApplicationDbContext context, Guid actor, bool claim)
        { await start.Task; return await CoverageStore(context).ChangeCaseClaim(new(actor, submissionId, Guid.NewGuid(), revision + 1, claim, new string('b', 64))); }
        Task<CaseClaimResult> release = Run(left, holder.Id, false);
        Task<CaseClaimResult> claim = Run(right, other.Id, true);
        start.SetResult();
        await Task.WhenAll(release, claim);
        (await release).Status.Should().Be(CaseClaimStatus.SUCCESS);
        (await claim).Status.Should().BeOneOf(CaseClaimStatus.HELD, CaseClaimStatus.STALE);
    }

    [Fact]
    public async Task Assignment_WhenRoleRevokedBeforeWrite_ShouldDenyClaim()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (_, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        var revision = await db.ModerationSubmissions.Where(s => s.Id == submissionId).Select(s => s.CaseRevision).SingleAsync();
        await using ApplicationDbContext revoker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await revoker.Database.BeginTransactionAsync();
        await revoker.Users.Where(u => u.Id == reviewer.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        await using ApplicationDbContext claimant = fixture.CreateDbContext();
        Task<CaseClaimResult> pending = CoverageStore(claimant).ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), revision, true, new string('a', 64)));
        await Task.Delay(200);
        pending.IsCompleted.Should().BeFalse();
        await tx.CommitAsync();
        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Status.Should().Be(CaseClaimStatus.FORBIDDEN);
        (await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId)).State.Should().Be(ModerationSubmissionState.SUBMITTED);
    }
    [Theory]
    [InlineData(AppRoles.USER)]
    [InlineData(AppRoles.ADMIN)]
    public async Task Assignment_WhenCallerCannotReview_ShouldDenyAtStore(string role)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (_, Guid submissionId) = await SeedSubmittedCase(db);
        User actor = await CoverageModerator(db, role);
        var provider = new EphemeralDataProtectionProvider();
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db), dataProtectionProvider: provider);
        (await store.GetModeratorQueue(new(actor.Id, null, CaseClaimFilter.ALL, 10, null))).Status.Should().Be(ReviewAccessStatus.FORBIDDEN);
        (await store.ChangeCaseClaim(new(actor.Id, submissionId, Guid.NewGuid(), 1, true, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.FORBIDDEN);
    }
    [Fact]
    public async Task Assignment_WhenReleaseIsUnclaimedOrTerminal_ShouldDistinguishReasons()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        var revision = await db.ModerationSubmissions.Where(s => s.Id == submissionId).Select(s => s.CaseRevision).SingleAsync();
        (await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), revision, false, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.NOT_CLAIMED);
        (await CoverageStore(db).WithdrawSubmission(new(seed.Source.Asset.AuthorId, submissionId, revision, Guid.NewGuid(), new string('b', 64), ModerationWithdrawalReason.SELLER_WITHDRAWAL))).Status.Should().Be(WithdrawSubmissionStatus.SUCCEEDED);
        (await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), revision, false, new string('c', 64)))).Status.Should().Be(CaseClaimStatus.NOT_ACTIVE);
    }
    [Fact]
    public async Task Assignment_WhenTrackedCaseIsStale_ShouldRefreshBeforeRelease()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (_, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        ModerationSubmission stale = await db.ModerationSubmissions.SingleAsync(s => s.Id == submissionId);
        var revision = stale.CaseRevision;
        await using ApplicationDbContext writer = fixture.CreateDbContext();
        (await CoverageStore(writer).ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), revision, true, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        (await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), revision + 1, false, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        ModerationSubmission result = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        result.State.Should().Be(ModerationSubmissionState.SUBMITTED);
        result.ClaimHolderId.Should().BeNull();
        result.ClaimedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Assignment_WhenWithdrawalAndClaimOccurInEitherOrder_ShouldPreserveTerminalState(bool claimFirst)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        var revision = await db.ModerationSubmissions.Where(s => s.Id == submissionId).Select(s => s.CaseRevision).SingleAsync();
        var claim = new CaseClaimChange(reviewer.Id, submissionId, Guid.NewGuid(), revision, true, new string('a', 64));
        if (claimFirst)
        { (await CoverageStore(db).ChangeCaseClaim(claim)).Status.Should().Be(CaseClaimStatus.SUCCESS); }
        var withdraw = new WithdrawSubmissionRequest(seed.Source.Asset.AuthorId, submissionId, revision + (claimFirst ? 1 : 0), Guid.NewGuid(), new string('b', 64), ModerationWithdrawalReason.SELLER_WITHDRAWAL);
        (await CoverageStore(db).WithdrawSubmission(withdraw)).Status.Should().Be(WithdrawSubmissionStatus.SUCCEEDED);
        if (!claimFirst)
        { (await CoverageStore(db).ChangeCaseClaim(claim)).Status.Should().Be(CaseClaimStatus.NOT_ACTIVE); }
        ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        row.State.Should().Be(ModerationSubmissionState.WITHDRAWN);
        row.ClaimHolderId.Should().BeNull();
        row.ClaimedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Assignment_WhenSupersessionAndClaimOccurInEitherOrder_ShouldPreserveTerminalState(bool claimFirst)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        var revision = await db.ModerationSubmissions.Where(s => s.Id == submissionId).Select(s => s.CaseRevision).SingleAsync();
        var claim = new CaseClaimChange(reviewer.Id, submissionId, Guid.NewGuid(), revision, true, new string('a', 64));
        if (claimFirst)
        { (await CoverageStore(db).ChangeCaseClaim(claim)).Status.Should().Be(CaseClaimStatus.SUCCESS); }
        SellerResponsesPageDto current = (await CoverageStore(db).GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        var edit = new SaveSellerEvidenceCommand(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, current.WorkspaceId,
            seed.ReportId, null, null, "Updated evidence", current.WorkspaceRevision, current.EvidenceRevision, Guid.NewGuid());
        (await new SaveSellerEvidenceCommandHandler(CoverageStore(db)).Handle(edit, CancellationToken.None)).IsSuccess.Should().BeTrue();
        if (!claimFirst)
        { (await CoverageStore(db).ChangeCaseClaim(claim)).Status.Should().Be(CaseClaimStatus.NOT_ACTIVE); }
        ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        row.State.Should().Be(ModerationSubmissionState.SUPERSEDED);
        row.ClaimHolderId.Should().BeNull();
        row.ClaimedAt.Should().BeNull();
    }

    [Fact]
    public async Task Assignment_WhenClaimRacesWithdrawal_ShouldSerialize()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        var revision = await db.ModerationSubmissions.Where(s => s.Id == submissionId).Select(s => s.CaseRevision).SingleAsync();
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        Task<CaseClaimResult> claim = CoverageStore(left).ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), revision, true, new string('a', 64)));
        Task<WithdrawSubmissionResult> withdrawal = CoverageStore(right).WithdrawSubmission(new(seed.Source.Asset.AuthorId, submissionId, revision, Guid.NewGuid(), new string('b', 64), ModerationWithdrawalReason.SELLER_WITHDRAWAL));
        await Task.WhenAll(claim, withdrawal);
        CaseClaimResult claimResult = await claim;
        WithdrawSubmissionResult withdrawalResult = await withdrawal;
        if (claimResult.Status == CaseClaimStatus.SUCCESS)
        { withdrawalResult.Status.Should().Be(WithdrawSubmissionStatus.STALE_CASE); }
        else
        { claimResult.Status.Should().Be(CaseClaimStatus.NOT_ACTIVE); withdrawalResult.Status.Should().Be(WithdrawSubmissionStatus.SUCCEEDED); }
    }

    [Fact]
    public async Task Assignment_WhenClaimRacesSupersession_ShouldSerialize()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        var revision = await db.ModerationSubmissions.Where(s => s.Id == submissionId).Select(s => s.CaseRevision).SingleAsync();
        SellerResponsesPageDto current = (await CoverageStore(db).GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        var edit = new SaveSellerEvidenceCommand(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, current.WorkspaceId,
            seed.ReportId, null, null, "Updated evidence", current.WorkspaceRevision, current.EvidenceRevision, Guid.NewGuid());
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        Task<CaseClaimResult> claim = CoverageStore(left).ChangeCaseClaim(new(reviewer.Id, submissionId, Guid.NewGuid(), revision, true, new string('a', 64)));
        Task<Ardalis.Result.Result<SellerEvidenceSavedDto>> editTask = new SaveSellerEvidenceCommandHandler(CoverageStore(right)).Handle(edit, CancellationToken.None);
        await Task.WhenAll(claim, editTask);
        (await claim).Status.Should().BeOneOf(CaseClaimStatus.SUCCESS, CaseClaimStatus.NOT_ACTIVE);
        (await editTask).IsSuccess.Should().BeTrue();
        ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        row.State.Should().Be(ModerationSubmissionState.SUPERSEDED);
        row.ClaimHolderId.Should().BeNull();
        row.ClaimedAt.Should().BeNull();
    }

    [Fact]
    public async Task Assignment_WhenClaimColumnsDoNotMatchState_ShouldFailDatabaseChecks()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (_, Guid submissionId) = await SeedSubmittedCase(db);
        User reviewer = await CoverageModerator(db);
        Func<Task> missingClaim = async () => await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE moderation_submissions SET "State" = 'IN_REVIEW' WHERE "Id" = {submissionId}""");
        Func<Task> claimOnSubmitted = async () => await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE moderation_submissions SET "ClaimHolderId" = {reviewer.Id}, "ClaimedAt" = {DateTimeOffset.UtcNow} WHERE "Id" = {submissionId}""");
        var nonexistent = Guid.NewGuid();
        Func<Task> missingHolder = async () => await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE moderation_submissions SET "State" = 'IN_REVIEW', "ClaimHolderId" = {nonexistent}, "ClaimedAt" = {DateTimeOffset.UtcNow} WHERE "Id" = {submissionId}""");
        (await missingClaim.Should().ThrowAsync<Npgsql.PostgresException>()).Which.SqlState.Should().Be(Npgsql.PostgresErrorCodes.CheckViolation);
        (await claimOnSubmitted.Should().ThrowAsync<Npgsql.PostgresException>()).Which.SqlState.Should().Be(Npgsql.PostgresErrorCodes.CheckViolation);
        (await missingHolder.Should().ThrowAsync<Npgsql.PostgresException>()).Which.SqlState.Should().Be(Npgsql.PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Assignment_WhenQueueRead_ShouldExcludeOwnerRejectTamperingAndRedact()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db);
        User moderator = await CoverageModerator(db);
        var provider = new EphemeralDataProtectionProvider();
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db),
            auditWriter: new AuditWriter(new AuditStore(db), Substitute.For<IAuditContextAccessor>(), NullLogger<AuditWriter>.Instance), dataProtectionProvider: provider);
        ReviewAccessResult<ModeratorQueuePage> result = await store.GetModeratorQueue(new(moderator.Id, null, CaseClaimFilter.ALL, 1, null));
        result.Status.Should().Be(ReviewAccessStatus.SUCCESS);
        result.Value!.Items.Select(i => i.SubmissionId).Should().Contain(submissionId);
        result.Value.Items.Single(i => i.SubmissionId == submissionId).Kind.Should().Be(ModerationSubmissionKind.STANDARD);
        var json = JsonSerializer.Serialize(result.Value);
        json.Should().NotContain("OwnExcerpt").And.NotContain("ForeignSourceId").And.NotContain("ReasonText").And.NotContain("SellerEvidenceDigest");
        CodeAnalysisReportFinding finding = await db.CodeAnalysisReportFindings.AsNoTracking().SingleAsync(f => f.Id == seed.Findings[0]);
        json.Should().NotContain(finding.OwnExcerpt).And.NotContain(finding.ForeignSourceId!);
        (await store.GetModeratorQueue(new(moderator.Id, null, CaseClaimFilter.ALL, 1, "tampered"))).Status.Should().Be(ReviewAccessStatus.INVALID);
        await db.Users.Where(u => u.Id == seed.Source.Asset.AuthorId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.MODERATOR));
        ReviewAccessResult<ModeratorQueuePage> owner = await store.GetModeratorQueue(new(seed.Source.Asset.AuthorId, null, CaseClaimFilter.ALL, 1, null));
        owner.Status.Should().Be(ReviewAccessStatus.SUCCESS);
        owner.Value!.Items.Should().BeEmpty();
        (await store.ChangeCaseClaim(new(seed.Source.Asset.AuthorId, submissionId, Guid.NewGuid(), 1, true, new string('c', 64)))).Status.Should().Be(CaseClaimStatus.FORBIDDEN);
    }

    [Fact]
    public async Task Assignment_WhenQueueChangesBetweenPages_ShouldKeepStableKeyset()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid firstId) = await SeedSubmittedCase(db);
        User moderator = await CoverageModerator(db);
        var provider = new EphemeralDataProtectionProvider();
        TimeProvider time = Substitute.For<TimeProvider>();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        time.GetUtcNow().Returns(_ => now);
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db), timeProvider: time, dataProtectionProvider: provider);
        async Task<Guid> AddCase(int number)
        {
            AssetVersion source = seed.Source.Version;
            var version = new AssetVersion
            {
                Id = Guid.NewGuid(),
                AssetId = source.AssetId,
                VersionNumber = number,
                IsCurrent = false,
                StorageKey = "queue-test/" + Guid.NewGuid().ToString("N"),
                FileName = source.FileName,
                ContentLength = source.ContentLength,
                ContentSha256 = source.ContentSha256,
                ReleaseNotes = source.ReleaseNotes,
                LicenseCode = source.LicenseCode,
                LicenseTemplateVersion = source.LicenseTemplateVersion,
                LicenseDisplayName = source.LicenseDisplayName,
                LicenseTerms = source.LicenseTerms,
                ProcessingStatus = AssetVersionProcessingStatus.READY,
                ProcessingUpdatedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var workspace = new AssetDraftWorkspace { Id = Guid.NewGuid(), AssetId = source.AssetId, AssetVersionId = version.Id, WorkspaceVersionScopeKey = version.Id, ScopeId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
            var submission = new ModerationSubmission
            {
                Id = Guid.NewGuid(),
                AssetId = source.AssetId,
                AssetVersionId = version.Id,
                WorkspaceId = workspace.Id,
                WorkspaceVersionScopeKey = version.Id,
                OwnerUserId = seed.Source.Asset.AuthorId,
                ContentSha256 = source.ContentSha256,
                DeclarationRevision = 1,
                MaterialMetadataRevision = 1,
                SellerEvidenceRevision = 0,
                PolicyVersion = ReviewAnalysisPolicy.KEY,
                SellerEvidenceDigest = new string('a', 64),
                State = ModerationSubmissionState.SUBMITTED,
                CaseRevision = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(number)
            };
            db.AssetVersions.Add(version);
            db.AssetDraftWorkspaces.Add(workspace);
            db.ModerationSubmissions.Add(submission);
            await db.SaveChangesAsync();
            return submission.Id;
        }
        Guid secondId = await AddCase(2);
        ReviewAccessResult<ModeratorQueuePage> first = await store.GetModeratorQueue(new(moderator.Id, null, CaseClaimFilter.ALL, 1, null));
        first.Value!.Items.Select(i => i.SubmissionId).Should().Equal(firstId);
        first.Value.NextCursor.Should().NotBeNull();
        (await CoverageStore(db).ChangeCaseClaim(new(moderator.Id, secondId, Guid.NewGuid(), 1, true, new string('d', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        Guid thirdId = await AddCase(3);
        (await store.GetModeratorQueue(new(moderator.Id, ModerationSubmissionState.IN_REVIEW, CaseClaimFilter.MINE, 50, null))).Value!.Items.Select(i => i.SubmissionId).Should().Equal(secondId);
        (await store.GetModeratorQueue(new(moderator.Id, null, CaseClaimFilter.UNCLAIMED, 50, null))).Value!.Items.Select(i => i.SubmissionId).Should().Equal(firstId, thirdId);
        ReviewAccessResult<ModeratorQueuePage> second = await store.GetModeratorQueue(new(moderator.Id, null, CaseClaimFilter.ALL, 1, first.Value.NextCursor));
        second.Value!.Items.Select(i => i.SubmissionId).Should().Equal(secondId);
        second.Value.NextCursor.Should().NotBeNull();
        ReviewAccessResult<ModeratorQueuePage> third = await store.GetModeratorQueue(new(moderator.Id, null, CaseClaimFilter.ALL, 1, second.Value.NextCursor));
        third.Value!.Items.Select(i => i.SubmissionId).Should().Equal(thirdId);
        third.Value.NextCursor.Should().BeNull();
        third.Value.Items.Single().HasOpenResponses.Should().BeFalse();
        (await store.GetModeratorQueue(new(moderator.Id, ModerationSubmissionState.SUBMITTED, CaseClaimFilter.ALL, 1, first.Value.NextCursor))).Status.Should().Be(ReviewAccessStatus.INVALID);
        var cursor = first.Value.NextCursor!;
        var tampered = (cursor[0] == 'A' ? 'B' : 'A') + cursor[1..];
        (await store.GetModeratorQueue(new(moderator.Id, null, CaseClaimFilter.ALL, 1, tampered))).Status.Should().Be(ReviewAccessStatus.INVALID);
        User other = await CoverageModerator(db);
        (await store.GetModeratorQueue(new(other.Id, null, CaseClaimFilter.ALL, 1, cursor))).Status.Should().Be(ReviewAccessStatus.INVALID);
        now = now.AddHours(2);
        (await store.GetModeratorQueue(new(moderator.Id, null, CaseClaimFilter.ALL, 1, cursor))).Status.Should().Be(ReviewAccessStatus.INVALID);
    }
    [Fact]
    public async Task Assignment_WhenQueuePageGrows_ShouldUseConstantQueriesAndMatchRequirements()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, Guid firstId) = await SeedSubmittedCase(db);
        User moderator = await CoverageModerator(db);
        AssetVersion source = seed.Source.Version;
        for (var number = 2; number <= 100; number++)
        {
            var version = new AssetVersion
            {
                Id = Guid.NewGuid(),
                AssetId = source.AssetId,
                VersionNumber = number,
                IsCurrent = false,
                StorageKey = "queue-count/" + Guid.NewGuid().ToString("N"),
                FileName = source.FileName,
                ContentLength = source.ContentLength,
                ContentSha256 = source.ContentSha256,
                ReleaseNotes = source.ReleaseNotes,
                LicenseCode = source.LicenseCode,
                LicenseTemplateVersion = source.LicenseTemplateVersion,
                LicenseDisplayName = source.LicenseDisplayName,
                LicenseTerms = source.LicenseTerms,
                ProcessingStatus = AssetVersionProcessingStatus.READY,
                ProcessingUpdatedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var workspace = new AssetDraftWorkspace { Id = Guid.NewGuid(), AssetId = source.AssetId, AssetVersionId = version.Id, WorkspaceVersionScopeKey = version.Id, ScopeId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
            var report = new CodeAnalysisReportHeader
            {
                Id = Guid.NewGuid(),
                AssetId = source.AssetId,
                AssetVersionId = version.Id,
                ContentSha256 = source.ContentSha256,
                PolicyVersion = ReviewAnalysisPolicy.KEY,
                InputRevision = 1,
                Purpose = CodeAnalysisReportPurpose.FIXTURE,
                ReviewEligible = false,
                IsFinalized = true,
                ReportSchemaVersion = 1,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var submission = new ModerationSubmission
            {
                Id = Guid.NewGuid(),
                AssetId = source.AssetId,
                AssetVersionId = version.Id,
                WorkspaceId = workspace.Id,
                WorkspaceVersionScopeKey = version.Id,
                OwnerUserId = seed.Source.Asset.AuthorId,
                ContentSha256 = source.ContentSha256,
                CodeAnalysisReportHeaderId = report.Id,
                DeclarationRevision = 1,
                MaterialMetadataRevision = 1,
                SellerEvidenceRevision = 0,
                PolicyVersion = ReviewAnalysisPolicy.KEY,
                SellerEvidenceDigest = new string('a', 64),
                State = ModerationSubmissionState.SUBMITTED,
                CaseRevision = 1,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(number)
            };
            db.AssetVersions.Add(version);
            db.AssetDraftWorkspaces.Add(workspace);
            db.CodeAnalysisReportHeaders.Add(report);
            db.ModerationSubmissions.Add(submission);
        }
        await db.SaveChangesAsync();
        var counter = new QueueCommandCounter();
        await using ApplicationDbContext observer = fixture.CreateDbContext(options => options.AddInterceptors(counter));
        var store = new ModerationFoundationStore(observer, new AssetStore(observer), new UserStore(observer), new EfUnitOfWork(observer), dataProtectionProvider: new EphemeralDataProtectionProvider());
        ReviewAccessResult<ModeratorQueuePage> small = await store.GetModeratorQueue(new(moderator.Id, null, CaseClaimFilter.ALL, 25, null));
        var smallCount = counter.Count;
        counter.Count = 0;
        ReviewAccessResult<ModeratorQueuePage> large = await store.GetModeratorQueue(new(moderator.Id, null, CaseClaimFilter.ALL, 100, null));
        counter.Count.Should().Be(smallCount);
        small.Value!.Items.Should().HaveCount(25);
        large.Value!.Items.Should().HaveCount(100);
        ModerationSubmission first = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == firstId);
        AssetDraftWorkspace firstWorkspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == first.WorkspaceId);
        firstWorkspace.SellerEvidenceHeadRevision = first.SellerEvidenceRevision;
        SellerResponseRequirementState requirements = await SellerResponseRequirements.Read(db, seed.ReportId, firstWorkspace, CancellationToken.None);
        ModeratorQueueItem item = large.Value.Items.Single(i => i.SubmissionId == firstId);
        item.MaterialFindingCount.Should().Be(requirements.Required.Count);
        item.HasOpenResponses.Should().Be(!requirements.Available || requirements.Outstanding > 0);
    }
}
