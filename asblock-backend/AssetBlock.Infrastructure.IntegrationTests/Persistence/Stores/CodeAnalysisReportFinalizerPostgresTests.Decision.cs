using System.Text.Json;
using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.SaveSellerEvidence;
using AssetBlock.Application.UseCases.Assets.SubmitAssetVersion;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Publication;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Publication;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NSubstitute;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

public sealed partial class CodeAnalysisReportFinalizerPostgresTests
{
    [Fact]
    public async Task Decision_WhenMigrationRevertedAndReapplied_ShouldRestoreCompletionReferenceColumn()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        IMigrator migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261031171312_AddModerationAssignment");
        var before = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'moderation_decisions' AND column_name = 'ManualPlanCompletionReference'").SingleAsync();
        before.Should().Be(0);
        await migrator.MigrateAsync();
        var after = await db.Database.SqlQueryRaw<int>("SELECT count(*)::int AS \"Value\" FROM information_schema.columns WHERE table_name = 'moderation_decisions' AND column_name = 'ManualPlanCompletionReference'").SingleAsync();
        after.Should().Be(1);
    }

    private sealed class FailDecisionOutbox : SaveChangesInterceptor
    {
        public bool Enabled { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Enabled && eventData.Context!.ChangeTracker.Entries<OutboxMessage>().Any(e => e.State == EntityState.Added && e.Entity.Type == OutboxMessageTypes.MODERATION_DECISION))
            { throw new DbUpdateException("Outbox write failed."); }
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }
    private static async Task<Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction> HoldDecisionAsset(ApplicationDbContext db, Guid assetId)
    {
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await db.Database.BeginTransactionAsync();
        await db.Database.SqlQuery<Guid>($"""SELECT "Id" AS "Value" FROM assets WHERE "Id" = {assetId} FOR UPDATE""").ToListAsync();
        return transaction;
    }
    private static async Task<(ResponseReviewSeed Seed, ModerationSubmission Case, User Reviewer)> SeedClaimedDecision(ApplicationDbContext db, string[]? tags = null)
    {
        (ResponseReviewSeed seed, Guid submissionId) = await SeedSubmittedCase(db, tags);
        User reviewer = await CoverageModerator(db);
        ModerationSubmission submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        CaseClaimResult claim = await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, submission.Id, Guid.NewGuid(), submission.CaseRevision, true, new string('a', 64)));
        claim.Status.Should().Be(CaseClaimStatus.SUCCESS);
        submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
        return (seed, submission, reviewer);
    }

    private static ModerationDecisionRequest DecisionRequest(ResponseReviewSeed seed, ModerationSubmission submission, User reviewer,
        ModerationDecisionOutcome outcome, FindingDispositionType disposition = FindingDispositionType.EXPLAINED) =>
        new(reviewer.Id, submission.Id, Guid.NewGuid(), new(submission.AssetVersionId, submission.ContentSha256, submission.CodeAnalysisReportHeaderId!.Value,
            submission.DeclarationRevision, submission.MaterialMetadataRevision, submission.PolicyVersion, submission.SellerEvidenceRevision, submission.CaseRevision),
            outcome, "Decision rationale", [new(seed.Findings[0], disposition, "Disposition rationale")], null, new string('d', 64));

    [Theory]
    [InlineData(FindingDispositionType.CHANGES_NEEDED)]
    [InlineData(FindingDispositionType.INSUFFICIENT_EVIDENCE)]
    public async Task Decision_WhenMaterialFindingUnresolved_ShouldBlockApprovalWithoutEffects(FindingDispositionType disposition)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE, disposition);
        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(input);
        result.Status.Should().Be(ModerationDecisionStatus.BLOCKED);
        result.ReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_DECISION_BLOCKED);
        (await db.ModerationDecisionRecords.AnyAsync()).Should().BeFalse();
        (await db.PublicationSnapshots.AnyAsync()).Should().BeFalse();
        (await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id)).State.Should().Be(ModerationSubmissionState.IN_REVIEW);
    }

    [Theory]
    [InlineData(FindingDispositionType.EXPLAINED)]
    [InlineData(FindingDispositionType.FALSE_MATCH)]
    [InlineData(FindingDispositionType.ACCEPTED_WITH_LIMITS)]
    public async Task Decision_WhenApproved_ShouldFreezeAndPromoteOnceAndReplay(FindingDispositionType disposition)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db, ["review-tag"]);
        Guid tagId = await db.Tags.Where(t => t.Name == "review-tag").Select(t => t.Id).SingleAsync();
        Tag staleTag = TestData.CreateTag("unapproved-tag");
        db.Tags.Add(staleTag);
        db.AssetTags.Add(new AssetTag { AssetId = submission.AssetId, TagId = tagId });
        db.AssetTags.Add(new AssetTag { AssetId = submission.AssetId, TagId = staleTag.Id });
        await db.SaveChangesAsync();
        var beforeRevision = (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId)).SearchRevision;
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE, disposition);
        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(input);
        result.Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        result.Saved!.Promoted.Should().BeTrue();
        (await CoverageStore(db).DecideModerationCase(input)).Saved!.Replayed.Should().BeTrue();
        (await CoverageStore(db).DecideModerationCase(input with { RequestDigest = new string('e', 64) })).Status.Should().Be(ModerationDecisionStatus.OPERATION_CONFLICT);
        (await CoverageStore(db).DecideModerationCase(input with { OperationId = Guid.NewGuid() })).ReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_DECISION_EXISTS);
        (await db.ModerationDecisionRecords.CountAsync()).Should().Be(1);
        (await db.PublicationSnapshots.CountAsync()).Should().Be(1);
        Asset asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId);
        asset.CurrentPublicationSnapshotId.Should().Be(result.Saved.PublicationSnapshotId);
        asset.SearchRevision.Should().Be(beforeRevision + 1);
        asset.Title.Should().Be("Review subject");
        asset.CategoryId.Should().Be(seed.Source.Asset.CategoryId);
        (await db.AssetTags.Where(t => t.AssetId == asset.Id).Select(t => t.Tag.Name).ToListAsync()).Should().Equal("review-tag");
        PublicationSnapshot snapshot = await db.PublicationSnapshots.AsNoTracking().SingleAsync();
        using var metadata = JsonDocument.Parse(snapshot.ApprovedMetadataJson);
        metadata.RootElement.GetProperty("title").GetString().Should().Be("Review subject");
        metadata.RootElement.GetProperty("tags")[0].GetString().Should().Be("review-tag");
        (await PublicationEligibilityQuery.PublicCatalogAssets(db).AnyAsync(a => a.Id == asset.Id)).Should().BeTrue();
        ModerationSubmission final = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        final.State.Should().Be(ModerationSubmissionState.APPROVED);
        final.ClaimHolderId.Should().BeNull();
        final.CaseRevision.Should().Be(submission.CaseRevision + 1);
        ModerationSubmissionHistoryEntry history = await db.ModerationSubmissionHistoryEntries.AsNoTracking().SingleAsync(h => h.SubmissionId == submission.Id && h.State == ModerationSubmissionState.APPROVED);
        history.CaseRevision.Should().Be(final.CaseRevision);
        history.Summary.Should().NotContain("rationale");
        OutboxMessage message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Type == OutboxMessageTypes.MODERATION_DECISION);
        message.Payload.Should().NotContain("rationale").And.NotContain("Review subject");
        AuditLog audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.CASE_DECIDED && a.ResourceId == submission.Id.ToString());
        audit.MetadataJson.Should().NotContain("rationale").And.NotContain("Review subject");
        await db.Users.Where(u => u.Id == reviewer.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        (await CoverageStore(db).DecideModerationCase(input)).Status.Should().Be(ModerationDecisionStatus.FORBIDDEN);
    }

    [Theory]
    [InlineData(ModerationDecisionOutcome.APPROVE)]
    [InlineData(ModerationDecisionOutcome.REQUEST_CHANGES)]
    [InlineData(ModerationDecisionOutcome.REJECT)]
    public async Task Decision_WhenNewerVersionIsPublic_ShouldNeverMovePointerBack(ModerationDecisionOutcome outcome)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        User author = await db.Users.SingleAsync(u => u.Id == seed.Source.Asset.AuthorId);
        Category category = await db.Categories.SingleAsync(c => c.Id == seed.Source.Asset.CategoryId);
        AssetVersion newer = TestData.CreateAssetVersion(seed.Source.Asset.Id, versionNumber: 2, isCurrent: false, processingStatus: AssetVersionProcessingStatus.READY);
        db.AssetVersions.Add(newer);
        await db.SaveChangesAsync();
        Asset trackedAsset = await db.Assets.SingleAsync(a => a.Id == submission.AssetId);
        PublicationSnapshot existing = await ApprovedPublicationTestBuilder.AttachTrustedApprovedPublication(db, trackedAsset, newer, author, category, "Public title");
        (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId)).CurrentPublicationSnapshotId.Should().Be(existing.Id);
        var searchRevision = (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId)).SearchRevision;
        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, outcome));
        result.Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        result.Saved!.Promoted.Should().BeFalse();
        Asset after = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId);
        after.CurrentPublicationSnapshotId.Should().Be(existing.Id);
        after.SearchRevision.Should().Be(searchRevision);
        (await db.PublicationSnapshots.CountAsync()).Should().Be(outcome == ModerationDecisionOutcome.APPROVE ? 2 : 1);
    }

    [Fact]
    public async Task Decision_WhenSameVersionAlreadyPublished_ShouldReplaceSnapshotWithApprovedMetadata()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db, ["review-tag"]);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var priorCase = new ModerationSubmission
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
            State = ModerationSubmissionState.APPROVED,
            CaseRevision = 1,
            CreatedAt = now
        };
        var priorSnapshot = new PublicationSnapshot
        {
            Id = Guid.NewGuid(),
            AssetId = submission.AssetId,
            AssetVersionId = submission.AssetVersionId,
            ContentSha256 = submission.ContentSha256,
            CodeAnalysisReportHeaderId = seed.ReportId,
            ModerationSubmissionId = priorCase.Id,
            PolicyVersion = submission.PolicyVersion,
            ApprovedMetadataJson = ApprovedPublicationMetadata.BuildJson("Old title", null, seed.Source.Asset.CategoryId, []),
            RightsReferenceJson = "{}",
            CreatedAt = now
        };
        var priorDecision = new ModerationDecisionRecord
        {
            Id = Guid.NewGuid(),
            SubmissionId = priorCase.Id,
            AssetId = submission.AssetId,
            AssetVersionId = submission.AssetVersionId,
            ModeratorUserId = reviewer.Id,
            Outcome = ModerationSubmissionState.APPROVED,
            PublicationSnapshotId = priorSnapshot.Id,
            CaseRevision = 1,
            Message = "Recorded decision",
            CreatedAt = now
        };
        db.ModerationSubmissions.Add(priorCase);
        db.PublicationSnapshots.Add(priorSnapshot);
        db.ModerationDecisionRecords.Add(priorDecision);
        Asset asset = await db.Assets.SingleAsync(a => a.Id == submission.AssetId);
        asset.CurrentPublicationSnapshotId = priorSnapshot.Id;
        asset.Title = "Old title";
        await db.SaveChangesAsync();
        var revision = asset.SearchRevision;
        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE));
        result.Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        result.Saved!.Promoted.Should().BeTrue();
        Asset after = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId);
        after.CurrentPublicationSnapshotId.Should().Be(result.Saved.PublicationSnapshotId).And.NotBe(priorSnapshot.Id);
        after.Title.Should().Be("Review subject");
        after.SearchRevision.Should().Be(revision + 1);
        (await db.PublicationSnapshots.CountAsync()).Should().Be(2);
        (await db.AssetTags.Where(t => t.AssetId == asset.Id).Select(t => t.Tag.Name).ToListAsync()).Should().Equal("review-tag");
    }

    [Theory]
    [InlineData("submitted")]
    [InlineData("review")]
    [InlineData("changes")]
    [InlineData("rejected")]
    public async Task Decision_WhenPendingVersionIsNotApproved_ShouldKeepOnlyEarlierPublicationAccessible(string state)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        await db.AssetVersions.Where(v => v.Id == submission.AssetVersionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.VersionNumber, 3));
        User author = await db.Users.SingleAsync(u => u.Id == seed.Source.Asset.AuthorId);
        Category category = await db.Categories.SingleAsync(c => c.Id == seed.Source.Asset.CategoryId);
        AssetVersion earlier = TestData.CreateAssetVersion(seed.Source.Asset.Id, storageKey: $"assets/{seed.Source.Asset.Id:N}/earlier.bin", versionNumber: 1, isCurrent: false, processingStatus: AssetVersionProcessingStatus.READY);
        db.AssetVersions.Add(earlier);
        await db.SaveChangesAsync();
        Asset trackedAsset = await db.Assets.SingleAsync(a => a.Id == submission.AssetId);
        PublicationSnapshot existing = await ApprovedPublicationTestBuilder.AttachTrustedApprovedPublication(db, trackedAsset, earlier, author, category, "Public title");
        if (state == "submitted")
        { (await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, submission.Id, Guid.NewGuid(), submission.CaseRevision, false, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS); }
        if (state is "changes" or "rejected")
        {
            ModerationDecisionOutcome outcome = state == "changes" ? ModerationDecisionOutcome.REQUEST_CHANGES : ModerationDecisionOutcome.REJECT;
            (await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, outcome))).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        }
        (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId)).CurrentPublicationSnapshotId.Should().Be(existing.Id);
        (await PublicationEligibilityQuery.PublicCatalogAssets(db).AnyAsync(a => a.Id == submission.AssetId)).Should().BeTrue();
        (await PublicationEligibilityQuery.SaleEligibleSnapshot(db, submission.AssetId, earlier.Id).AnyAsync()).Should().BeTrue();
        (await PublicationEligibilityQuery.SaleEligibleSnapshot(db, submission.AssetId, submission.AssetVersionId).AnyAsync()).Should().BeFalse();
        (await PublicationEligibilityQuery.BuyerAccessibleVersions(db, submission.AssetId, 1).AnyAsync(v => v.Id == earlier.Id)).Should().BeTrue();
        (await PublicationEligibilityQuery.BuyerAccessibleVersions(db, submission.AssetId, 1).AnyAsync(v => v.Id == submission.AssetVersionId)).Should().BeFalse();
        var assetStore = new AssetStore(db);
        (await assetStore.IsPinnedSaleOfferingValid(submission.AssetId, earlier.Id, existing.Id)).Should().BeTrue();
        (await assetStore.IsPinnedSaleOfferingValid(submission.AssetId, submission.AssetVersionId, existing.Id)).Should().BeFalse();
        (await assetStore.IsApprovedBuyerDownloadVersion(submission.AssetId, earlier.Id, 1)).Should().BeTrue();
        (await assetStore.IsApprovedBuyerDownloadVersion(submission.AssetId, submission.AssetVersionId, 1)).Should().BeFalse();
    }

    [Theory]
    [InlineData(ModerationDecisionOutcome.REQUEST_CHANGES, ModerationSubmissionState.CHANGES_REQUESTED, FindingDispositionType.CHANGES_NEEDED)]
    [InlineData(ModerationDecisionOutcome.REJECT, ModerationSubmissionState.REJECTED, FindingDispositionType.INSUFFICIENT_EVIDENCE)]
    public async Task Decision_WhenNotApproved_ShouldNotPublish(ModerationDecisionOutcome outcome, ModerationSubmissionState state, FindingDispositionType disposition)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, outcome, disposition));
        result.Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        result.Saved!.State.Should().Be(state);
        result.Saved.Promoted.Should().BeFalse();
        (await db.PublicationSnapshots.AnyAsync()).Should().BeFalse();
        (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId)).CurrentPublicationSnapshotId.Should().BeNull();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("content")]
    [InlineData("report")]
    [InlineData("declaration")]
    [InlineData("material")]
    [InlineData("policy")]
    [InlineData("evidence")]
    [InlineData("case")]
    public async Task Decision_WhenExpectedTupleChanges_ShouldRejectWithoutEffects(string member)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        DecisionBasis basis = input.Basis;
        basis = member switch
        {
            "version" => basis with { VersionId = Guid.NewGuid() },
            "content" => basis with { ContentSha256 = new string('c', 64) },
            "report" => basis with { AnalysisReportId = Guid.NewGuid() },
            "declaration" => basis with { DeclarationRevision = basis.DeclarationRevision + 1 },
            "material" => basis with { MaterialMetadataRevision = basis.MaterialMetadataRevision + 1 },
            "policy" => basis with { PolicyVersion = "unmatched" },
            "evidence" => basis with { SellerEvidenceRevision = basis.SellerEvidenceRevision + 1 },
            _ => basis with { CaseRevision = basis.CaseRevision + 1 }
        };
        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(input with { Basis = basis });
        result.Status.Should().Be(ModerationDecisionStatus.STALE);
        result.ReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_CASE_STALE);
        (await db.ModerationDecisionRecords.AnyAsync()).Should().BeFalse();
        (await db.PublicationSnapshots.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData("content")]
    [InlineData("report")]
    [InlineData("declaration")]
    [InlineData("material")]
    [InlineData("evidence")]
    [InlineData("policy")]
    [InlineData("evidenceDigest")]
    public async Task Decision_WhenCurrentTupleChanges_ShouldRejectWithoutEffects(string member)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        if (member == "content")
        { await db.AssetVersions.Where(v => v.Id == submission.AssetVersionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.ContentSha256, new string('c', 64))); }
        else if (member == "report")
        { await db.AssetVersions.Where(v => v.Id == submission.AssetVersionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.SelectedCodeAnalysisReportId, (Guid?)null)); }
        else if (member == "declaration")
        { await db.AssetDraftWorkspaces.Where(w => w.Id == submission.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(w => w.SourceDeclarationHeadRevision, submission.DeclarationRevision + 1)); }
        else if (member == "material")
        { await db.AssetDraftWorkspaces.Where(w => w.Id == submission.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(w => w.MaterialMetadataHeadRevision, submission.MaterialMetadataRevision + 1)); }
        else if (member == "evidence")
        { await db.AssetDraftWorkspaces.Where(w => w.Id == submission.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(w => w.SellerEvidenceHeadRevision, submission.SellerEvidenceRevision + 1)); }
        else if (member == "evidenceDigest")
        { await db.AssetSellerEvidenceRevisions.Where(r => r.WorkspaceId == submission.WorkspaceId && r.Revision == submission.SellerEvidenceRevision).ExecuteUpdateAsync(s => s.SetProperty(r => r.ContentDigest, new string('f', 64))); }
        else
        { await db.CodeAnalysisReportHeaders.Where(h => h.Id == seed.ReportId).ExecuteUpdateAsync(s => s.SetProperty(h => h.PolicyVersion, "unmatched")); }
        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE));
        result.Status.Should().Be(member == "policy" ? ModerationDecisionStatus.BLOCKED : ModerationDecisionStatus.STALE);
        (await db.ModerationDecisionRecords.AnyAsync()).Should().BeFalse();
        (await db.PublicationSnapshots.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicate")]
    [InlineData("foreign")]
    public async Task Decision_WhenDispositionSetIsInvalid_ShouldReject(string invalid)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        input = invalid switch
        {
            "missing" => input with { Dispositions = [] },
            "duplicate" => input with { Dispositions = [input.Dispositions[0], input.Dispositions[0]] },
            _ => input with { Dispositions = [input.Dispositions[0], new(Guid.NewGuid(), FindingDispositionType.EXPLAINED, "Reason")] }
        };
        (await CoverageStore(db).DecideModerationCase(input)).ReasonCode.Should().Be(ErrorCodes.ERR_MODERATION_DISPOSITIONS_INVALID);
        (await db.ModerationDecisionRecords.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("length")]
    public async Task Decision_WhenInputIsMalformed_ShouldReturnValidation(string invalid)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        input = invalid == "null" ? input with { Dispositions = [null!] } : input with { Rationale = new string('x', 2001) };
        (await CoverageStore(db).DecideModerationCase(input)).ReasonCode.Should().Be(ErrorCodes.ERR_VALIDATION_FAILED);
        (await db.ModerationDecisionRecords.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Decision_WhenSubmittedTagIsRenamed_ShouldAllowRequestingChanges()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db, ["review-tag"]);
        await db.Tags.Where(t => t.Name == "review-tag").ExecuteUpdateAsync(s => s.SetProperty(t => t.Name, "renamed-tag"));
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        ModerationDecisionResult blocked = await CoverageStore(db).DecideModerationCase(input);
        blocked.ReasonCode.Should().Be(ErrorCodes.ERR_PUBLICATION_METADATA_UNAVAILABLE);
        (await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id)).State.Should().Be(ModerationSubmissionState.IN_REVIEW);
        (await db.ModerationFindingDispositions.AnyAsync()).Should().BeFalse();
        (await CoverageStore(db).DecideModerationCase(input with { OperationId = Guid.NewGuid(), Outcome = ModerationDecisionOutcome.REQUEST_CHANGES })).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("null")]
    public async Task Decision_WhenSubmittedMetadataIsUnavailable_ShouldAllowRequestingChanges(string kind)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        Category removedCategory = TestData.CreateCategory("Other category", "other-category");
        db.Categories.Add(removedCategory);
        await db.SaveChangesAsync();
        Guid categoryId = removedCategory.Id;
        await db.Categories.Where(c => c.Id == categoryId).ExecuteDeleteAsync();
        var payload = kind == "null" ? "null" : JsonSerializer.Serialize(new { title = "Review subject", description = (string?)null, categoryId, tags = Array.Empty<string>() });
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload)));
        await db.AssetMaterialMetadataRevisions.Where(m => m.WorkspaceId == submission.WorkspaceId && m.Revision == submission.MaterialMetadataRevision)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.PayloadJson, payload).SetProperty(m => m.ContentDigest, digest));
        await db.CodeAnalysisRuns.Where(r => r.Id == seed.Source.RunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.MaterialDigest, digest));
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        (await CoverageStore(db).DecideModerationCase(input)).ReasonCode.Should().Be(ErrorCodes.ERR_PUBLICATION_METADATA_UNAVAILABLE);
        (await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id)).State.Should().Be(ModerationSubmissionState.IN_REVIEW);
        (await db.ModerationFindingDispositions.AnyAsync()).Should().BeFalse();
        (await CoverageStore(db).DecideModerationCase(input with { OperationId = Guid.NewGuid(), Outcome = ModerationDecisionOutcome.REQUEST_CHANGES })).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
    }

    [Fact]
    public async Task Decision_WhenNonMaterialFindingNeedsChanges_ShouldStillAllowApproval()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 2);
        await db.Database.ExecuteSqlInterpolatedAsync($"""UPDATE code_analysis_report_findings SET "RetrievalEvidenceJson" = jsonb_set("RetrievalEvidenceJson", ARRAY['material'], 'false'::jsonb) WHERE "Id" = {seed.Findings[1]}""");
        ModerationFoundationStore store = CoverageStore(db);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        (await new SaveSellerEvidenceCommandHandler(store).Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.NO_EVIDENCE, ""), CancellationToken.None)).IsSuccess.Should().BeTrue();
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Result<Guid> submitted = await new SubmitAssetVersionCommandHandler(store, ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        submitted.IsSuccess.Should().BeTrue();
        User reviewer = await CoverageModerator(db);
        ModerationSubmission submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submitted.Value);
        (await store.ChangeCaseClaim(new(reviewer.Id, submission.Id, Guid.NewGuid(), submission.CaseRevision, true, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submitted.Value);
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        input = input with { Dispositions = [input.Dispositions[0], new(seed.Findings[1], FindingDispositionType.CHANGES_NEEDED, "Non-material finding")] };
        (await store.DecideModerationCase(input)).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        (await db.ModerationFindingDispositions.CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Decision_WhenAuditFails_ShouldRollbackDecisionSnapshotAndDispositions()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        IAuditWriter writer = Substitute.For<IAuditWriter>();
        writer.Write(Arg.Any<AssetBlock.Domain.Core.Dto.Audit.AuditEvent>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("private sentinel")));
        ModerationDecisionResult result = await CoverageStore(db, writer).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE));
        result.Status.Should().Be(ModerationDecisionStatus.AUDIT_UNAVAILABLE);
        await using ApplicationDbContext observer = fixture.CreateDbContext();
        (await observer.ModerationDecisionRecords.AnyAsync()).Should().BeFalse();
        (await observer.PublicationSnapshots.AnyAsync()).Should().BeFalse();
        (await observer.ModerationFindingDispositions.AnyAsync()).Should().BeFalse();
        (await observer.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId)).CurrentPublicationSnapshotId.Should().BeNull();
    }
    [Fact]
    public async Task Decision_WhenOutboxWriteFails_ShouldRollbackDecisionSnapshotAndPointer()
    {
        var failure = new FailDecisionOutbox();
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext(options => options.AddInterceptors(failure));
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        failure.Enabled = true;
        Func<Task> operation = async () => await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE));
        await operation.Should().ThrowAsync<DbUpdateException>();
        await using ApplicationDbContext observer = fixture.CreateDbContext();
        (await observer.ModerationDecisionRecords.AnyAsync()).Should().BeFalse();
        (await observer.PublicationSnapshots.AnyAsync()).Should().BeFalse();
        (await observer.ModerationFindingDispositions.AnyAsync()).Should().BeFalse();
        (await observer.Assets.AsNoTracking().SingleAsync(a => a.Id == submission.AssetId)).CurrentPublicationSnapshotId.Should().BeNull();
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("user")]
    [InlineData("other")]
    [InlineData("revoked")]
    [InlineData("missing")]
    public async Task Decision_WhenActorCannotDecide_ShouldDeny(string kind)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        User actor = kind switch
        {
            "admin" => await CoverageModerator(db, AppRoles.ADMIN),
            "user" => await CoverageModerator(db, AppRoles.USER),
            "other" => await CoverageModerator(db),
            _ => reviewer
        };
        if (kind == "owner")
        { await db.Users.Where(u => u.Id == seed.Source.Asset.AuthorId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.MODERATOR)); }
        if (kind == "revoked")
        { await db.Users.Where(u => u.Id == reviewer.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER)); }
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE) with { ActorId = kind == "owner" ? seed.Source.Asset.AuthorId : kind == "missing" ? Guid.NewGuid() : actor.Id };
        ModerationDecisionStatus status = (await CoverageStore(db).DecideModerationCase(input)).Status;
        status.Should().Be(kind == "other" ? ModerationDecisionStatus.NOT_HOLDER : ModerationDecisionStatus.FORBIDDEN);
        (await db.ModerationDecisionRecords.AnyAsync()).Should().BeFalse();
    }
    [Theory]
    [InlineData("restricted")]
    [InlineData("archive")]
    [InlineData("integrity")]
    [InlineData("stage")]
    public async Task Decision_WhenSafetyOrMandatoryStageFails_ShouldNeverApprove(string failure)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        if (failure != "stage")
        {
            AssetVersionSafetyRestriction restriction = failure == "restricted" ? AssetVersionSafetyRestriction.INFECTED : failure == "archive" ? AssetVersionSafetyRestriction.UNSAFE_ARCHIVE : AssetVersionSafetyRestriction.INTEGRITY_MISMATCH;
            await db.AssetVersions.Where(v => v.Id == submission.AssetVersionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.SafetyRestriction, restriction)
                .SetProperty(v => v.SafetyRestrictionReason, "SAFETY_CHECK_FAILED").SetProperty(v => v.SafetyRestrictedAt, DateTimeOffset.UtcNow));
        }
        else
        { await db.CodeAnalysisReportStages.Where(s => s.ReportId == seed.ReportId && s.Stage == "TOKEN").ExecuteUpdateAsync(s => s.SetProperty(v => v.Outcome, CodeAnalysisStageOutcome.FAILED)); }
        ModerationDecisionResult result = await CoverageStore(db).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE));
        result.Status.Should().Be(ModerationDecisionStatus.BLOCKED);
        result.ReasonCode.Should().Be(failure != "stage" ? ErrorCodes.ERR_VERSION_FILE_CHECKS_INCOMPLETE : ErrorCodes.ERR_ANALYSIS_REPORT_NOT_TRUSTED);
        (await db.PublicationSnapshots.AnyAsync()).Should().BeFalse();
    }
    [Fact]
    public async Task Decision_WhenNoFindings_ShouldStillRequireAndRecordExplicitApproval()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        await db.CodeAnalysisReportFindings.Where(f => f.ReportId == seed.ReportId).ExecuteDeleteAsync();
        await db.CodeAnalysisReportHeaders.Where(h => h.Id == seed.ReportId).ExecuteUpdateAsync(s => s.SetProperty(h => h.FindingCount, 0));
        ModerationFoundationStore store = CoverageStore(db);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Result<Guid> submissionResult = await new SubmitAssetVersionCommandHandler(store, ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        submissionResult.IsSuccess.Should().BeTrue();
        User reviewer = await CoverageModerator(db);
        ModerationSubmission submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionResult.Value);
        (await store.ChangeCaseClaim(new(reviewer.Id, submission.Id, Guid.NewGuid(), submission.CaseRevision, true, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE) with { Dispositions = [] };
        (await store.DecideModerationCase(input)).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        (await db.ModerationDecisionRecords.CountAsync()).Should().Be(1);
        (await db.ModerationFindingDispositions.CountAsync()).Should().Be(0);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_WhenCoverageIsPartial_ShouldRequireCurrentAssessmentAndCompletedPlan(bool unsupported)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User assessor = await CoverageModerator(db);
        if (unsupported)
        { await db.CodeAnalysisReportHeaders.Where(h => h.Id == seed.ReportId).ExecuteUpdateAsync(s => s.SetProperty(h => h.CoverageLevel, CodeAnalysisCoverageLevel.UNSUPPORTED)); }
        CoverageReviewSaved requested = await CreateCoverageRequest(store, seed);
        (await AssessReadableCoverage(store, assessor.Id, requested)).Status.Should().Be(ReviewAccessStatus.SUCCESS);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        (await new SaveSellerEvidenceCommandHandler(store).Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.NO_EVIDENCE, ""), CancellationToken.None)).IsSuccess.Should().BeTrue();
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Result<Guid> submitted = await new SubmitAssetVersionCommandHandler(store, ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        submitted.IsSuccess.Should().BeTrue();
        User reviewer = await CoverageModerator(db);
        ModerationSubmission submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submitted.Value);
        (await store.ChangeCaseClaim(new(reviewer.Id, submission.Id, Guid.NewGuid(), submission.CaseRevision, true, new string('a', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submitted.Value);
        ModerationDecisionRequest input = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        (await store.DecideModerationCase(input)).Status.Should().Be(ModerationDecisionStatus.BLOCKED);
        (await store.DecideModerationCase(input with { ManualPlanCompletionReference = "different-reference" })).ReasonCode.Should().Be(ErrorCodes.ERR_REVIEW_PLAN_REFERENCE_MISMATCH);
        await db.Users.Where(u => u.Id == assessor.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        (await store.DecideModerationCase(input with { ManualPlanCompletionReference = "manual-review/documentation-complete" })).Status.Should().Be(ModerationDecisionStatus.BLOCKED);
        await db.Users.Where(u => u.Id == assessor.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.MODERATOR));
        (await store.DecideModerationCase(input with { ManualPlanCompletionReference = "manual-review/documentation-complete" })).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        (await db.ModerationDecisionRecords.SingleAsync()).ManualPlanCompletionReference.Should().Be("manual-review/documentation-complete");
    }

    [Fact]
    public async Task Decision_WhenRoleIsRevokedBeforeWrite_ShouldDeny()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        await using ApplicationDbContext revoker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction tx = await revoker.Database.BeginTransactionAsync();
        await revoker.Users.Where(u => u.Id == reviewer.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        await using ApplicationDbContext decider = fixture.CreateDbContext();
        Task<ModerationDecisionResult> pending = CoverageStore(decider).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE));
        await Task.Delay(200);
        pending.IsCompleted.Should().BeFalse();
        await tx.CommitAsync();
        (await pending.WaitAsync(TimeSpan.FromSeconds(10))).Status.Should().Be(ModerationDecisionStatus.FORBIDDEN);
        (await db.PublicationSnapshots.AnyAsync()).Should().BeFalse();
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
    public async Task Decision_WhenOldAndNewHoldersRaceAfterTakeover_ShouldOnlyDecideOnce(int _)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User old) = await SeedClaimedDecision(db);
        User current = await CoverageModerator(db);
        await db.Users.Where(u => u.Id == old.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        (await CoverageStore(db).ChangeCaseClaim(new(current.Id, submission.Id, Guid.NewGuid(), submission.CaseRevision, true, new string('b', 64)))).Status.Should().Be(CaseClaimStatus.SUCCESS);
        submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        await using ApplicationDbContext blocker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction held = await HoldDecisionAsset(blocker, submission.AssetId);
        await left.Database.OpenConnectionAsync();
        await right.Database.OpenConnectionAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ModerationDecisionResult> Run(ApplicationDbContext context, User actor)
        { await start.Task; return await CoverageStore(context).DecideModerationCase(DecisionRequest(seed, submission, actor, ModerationDecisionOutcome.APPROVE)); }
        Task<ModerationDecisionResult> first = Run(left, old);
        Task<ModerationDecisionResult> second = Run(right, current);
        start.SetResult();
        await Task.Delay(200);
        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        await held.CommitAsync();
        ModerationDecisionResult[] results = await Task.WhenAll(first, second);
        results.Count(r => r.Status == ModerationDecisionStatus.SUCCEEDED).Should().Be(1);
        results.Count(r => r.Status == ModerationDecisionStatus.FORBIDDEN).Should().Be(1);
        (await db.ModerationDecisionRecords.CountAsync()).Should().Be(1);
        (await db.PublicationSnapshots.CountAsync()).Should().Be(1);
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
    public async Task Decision_WhenWithdrawalRaces_ShouldOnlyOneTransitionWin(int _)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        await using ApplicationDbContext blocker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction held = await HoldDecisionAsset(blocker, submission.AssetId);
        await left.Database.OpenConnectionAsync();
        await right.Database.OpenConnectionAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ModerationDecisionResult> Decide()
        { await start.Task; return await CoverageStore(left).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE)); }
        async Task<WithdrawSubmissionResult> Withdraw()
        { await start.Task; return await CoverageStore(right).WithdrawSubmission(new(seed.Source.Asset.AuthorId, submission.Id, submission.CaseRevision, Guid.NewGuid(), new string('b', 64), ModerationWithdrawalReason.SELLER_WITHDRAWAL)); }
        Task<ModerationDecisionResult> decision = Decide();
        Task<WithdrawSubmissionResult> withdrawal = Withdraw();
        start.SetResult();
        await Task.Delay(200);
        decision.IsCompleted.Should().BeFalse();
        withdrawal.IsCompleted.Should().BeFalse();
        await held.CommitAsync();
        await Task.WhenAll(decision, withdrawal);
        var decisionWon = (await decision).Status == ModerationDecisionStatus.SUCCEEDED;
        ((await withdrawal).Status == WithdrawSubmissionStatus.SUCCEEDED).Should().Be(!decisionWon);
        if (!decisionWon)
        { (await decision).Status.Should().Be(ModerationDecisionStatus.NOT_ACTIVE); }
        (await db.ModerationDecisionRecords.CountAsync()).Should().Be(decisionWon ? 1 : 0);
        (await db.PublicationSnapshots.CountAsync()).Should().Be(decisionWon ? 1 : 0);
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
    public async Task Decision_WhenReleaseRaces_ShouldOnlyOneTransitionWin(int _)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        await using ApplicationDbContext blocker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction held = await HoldDecisionAsset(blocker, submission.AssetId);
        await left.Database.OpenConnectionAsync();
        await right.Database.OpenConnectionAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ModerationDecisionResult> Decide()
        { await start.Task; return await CoverageStore(left).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE)); }
        async Task<CaseClaimResult> Release()
        { await start.Task; return await CoverageStore(right).ChangeCaseClaim(new(reviewer.Id, submission.Id, Guid.NewGuid(), submission.CaseRevision, false, new string('b', 64))); }
        Task<ModerationDecisionResult> decision = Decide();
        Task<CaseClaimResult> release = Release();
        start.SetResult();
        await Task.Delay(200);
        decision.IsCompleted.Should().BeFalse();
        release.IsCompleted.Should().BeFalse();
        await held.CommitAsync();
        await Task.WhenAll(decision, release);
        var decisionWon = (await decision).Status == ModerationDecisionStatus.SUCCEEDED;
        ((await release).Status == CaseClaimStatus.SUCCESS).Should().Be(!decisionWon);
        if (!decisionWon)
        { (await decision).Status.Should().Be(ModerationDecisionStatus.NOT_ACTIVE); }
        (await db.ModerationDecisionRecords.CountAsync()).Should().Be(decisionWon ? 1 : 0);
        (await db.PublicationSnapshots.CountAsync()).Should().Be(decisionWon ? 1 : 0);
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
    public async Task Decision_WhenAnotherClaimRaces_ShouldKeepHolderAndPublishOnce(int _)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        User other = await CoverageModerator(db);
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        await using ApplicationDbContext blocker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction held = await HoldDecisionAsset(blocker, submission.AssetId);
        await left.Database.OpenConnectionAsync();
        await right.Database.OpenConnectionAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ModerationDecisionResult> Decide()
        { await start.Task; return await CoverageStore(left).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE)); }
        async Task<CaseClaimResult> Claim()
        { await start.Task; return await CoverageStore(right).ChangeCaseClaim(new(other.Id, submission.Id, Guid.NewGuid(), submission.CaseRevision, true, new string('b', 64))); }
        Task<ModerationDecisionResult> decision = Decide();
        Task<CaseClaimResult> claim = Claim();
        start.SetResult();
        await Task.Delay(200);
        decision.IsCompleted.Should().BeFalse();
        claim.IsCompleted.Should().BeFalse();
        await held.CommitAsync();
        await Task.WhenAll(decision, claim);
        (await decision).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED);
        (await claim).Status.Should().BeOneOf(CaseClaimStatus.HELD, CaseClaimStatus.NOT_ACTIVE);
        (await db.ModerationDecisionRecords.CountAsync()).Should().Be(1);
        (await db.PublicationSnapshots.CountAsync()).Should().Be(1);
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
    public async Task Decision_WhenEvidenceEditRaces_ShouldPreserveCommittedTransition(int _)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        SellerResponsesPageDto current = (await CoverageStore(db).GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        var edit = new SaveSellerEvidenceCommand(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, current.WorkspaceId,
            seed.ReportId, null, null, "Updated evidence", current.WorkspaceRevision, current.EvidenceRevision, Guid.NewGuid());
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        await using ApplicationDbContext blocker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction held = await HoldDecisionAsset(blocker, submission.AssetId);
        await left.Database.OpenConnectionAsync();
        await right.Database.OpenConnectionAsync();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<ModerationDecisionResult> Decide()
        { await start.Task; return await CoverageStore(left).DecideModerationCase(DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE)); }
        async Task<Result<SellerEvidenceSavedDto>> Edit()
        { await start.Task; return await new SaveSellerEvidenceCommandHandler(CoverageStore(right)).Handle(edit, CancellationToken.None); }
        Task<ModerationDecisionResult> decision = Decide();
        Task<Result<SellerEvidenceSavedDto>> evidence = Edit();
        start.SetResult();
        await Task.Delay(200);
        decision.IsCompleted.Should().BeFalse();
        evidence.IsCompleted.Should().BeFalse();
        await held.CommitAsync();
        await Task.WhenAll(decision, evidence);
        (await evidence).IsSuccess.Should().BeTrue();
        var decisionWon = (await decision).Status == ModerationDecisionStatus.SUCCEEDED;
        ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        row.State.Should().Be(decisionWon ? ModerationSubmissionState.APPROVED : ModerationSubmissionState.SUPERSEDED);
        (await db.PublicationSnapshots.CountAsync()).Should().Be(decisionWon ? 1 : 0);
    }

    [Theory]
    [InlineData("release", true)]
    [InlineData("release", false)]
    [InlineData("withdraw", true)]
    [InlineData("withdraw", false)]
    [InlineData("edit", true)]
    [InlineData("edit", false)]
    [InlineData("claim", true)]
    [InlineData("claim", false)]
    public async Task Decision_WhenCompetingTransitionOccursFirstOrSecond_ShouldRespectOrder(string competing, bool decisionFirst)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (ResponseReviewSeed seed, ModerationSubmission submission, User reviewer) = await SeedClaimedDecision(db);
        ModerationDecisionRequest decision = DecisionRequest(seed, submission, reviewer, ModerationDecisionOutcome.APPROVE);
        SellerResponsesPageDto current = (await CoverageStore(db).GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        var edit = new SaveSellerEvidenceCommand(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, current.WorkspaceId,
            seed.ReportId, null, null, "Updated evidence", current.WorkspaceRevision, current.EvidenceRevision, Guid.NewGuid());
        if (decisionFirst)
        { (await CoverageStore(db).DecideModerationCase(decision)).Status.Should().Be(ModerationDecisionStatus.SUCCEEDED); }
        if (competing == "release")
        {
            CaseClaimResult released = await CoverageStore(db).ChangeCaseClaim(new(reviewer.Id, submission.Id, Guid.NewGuid(), submission.CaseRevision, false, new string('b', 64)));
            released.Status.Should().Be(decisionFirst ? CaseClaimStatus.NOT_ACTIVE : CaseClaimStatus.SUCCESS);
        }
        else if (competing == "withdraw")
        {
            WithdrawSubmissionResult withdrawn = await CoverageStore(db).WithdrawSubmission(new(seed.Source.Asset.AuthorId, submission.Id, submission.CaseRevision, Guid.NewGuid(), new string('b', 64), ModerationWithdrawalReason.SELLER_WITHDRAWAL));
            withdrawn.Status.Should().Be(decisionFirst ? WithdrawSubmissionStatus.NOT_ACTIVE : WithdrawSubmissionStatus.SUCCEEDED);
        }
        else if (competing == "claim")
        {
            User other = await CoverageModerator(db);
            CaseClaimResult claimed = await CoverageStore(db).ChangeCaseClaim(new(other.Id, submission.Id, Guid.NewGuid(), submission.CaseRevision, true, new string('c', 64)));
            claimed.Status.Should().Be(decisionFirst ? CaseClaimStatus.NOT_ACTIVE : CaseClaimStatus.HELD);
        }
        else
        { (await new SaveSellerEvidenceCommandHandler(CoverageStore(db)).Handle(edit, CancellationToken.None)).IsSuccess.Should().BeTrue(); }
        if (!decisionFirst)
        { (await CoverageStore(db).DecideModerationCase(decision)).Status.Should().Be(competing == "claim" ? ModerationDecisionStatus.SUCCEEDED : ModerationDecisionStatus.NOT_ACTIVE); }
        var decisionWon = decisionFirst || competing == "claim";
        ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submission.Id);
        row.State.Should().Be(decisionWon ? ModerationSubmissionState.APPROVED : competing switch { "release" => ModerationSubmissionState.SUBMITTED, "withdraw" => ModerationSubmissionState.WITHDRAWN, _ => ModerationSubmissionState.SUPERSEDED });
        (await db.PublicationSnapshots.CountAsync()).Should().Be(decisionWon ? 1 : 0);
    }
}
