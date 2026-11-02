using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using static AssetBlock.Domain.Core.Constants.DraftWorkspaceVersionScopes;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class ModerationFoundationStorePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task EnsurePreUploadWorkspace_WhenNoVersion_ShouldAllowDraftWithoutBytes()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);

        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        AssetDraftWorkspaceSnapshot workspace = await store.EnsurePreUploadWorkspace(asset.Id, author.Id);

        workspace.AssetVersionId.Should().BeNull();
        (await db.AssetVersions.CountAsync(v => v.AssetId == asset.Id)).Should().Be(0);
    }

    [Fact]
    public async Task TryCommitIdempotentMutation_WhenSameOperationIdAndDigest_ShouldReplay()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = TestData.CreateUser();
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        var operationId = Guid.NewGuid();
        IdempotentMutationResult first = await store.TryCommitIdempotentMutation(
            user.Id,
            ModerationOperationKinds.DRAFT_SAVE,
            operationId,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            """{"ok":true}""");

        IdempotentMutationResult second = await store.TryCommitIdempotentMutation(
            user.Id,
            ModerationOperationKinds.DRAFT_SAVE,
            operationId,
            "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            """{"ok":true}""");

        first.Status.Should().Be(IdempotentMutationStatus.COMMITTED);
        second.Status.Should().Be(IdempotentMutationStatus.REPLAYED);
        (await db.JsonMutationIdempotencyRecords.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AttemptGuardedSubmission_WhenReplayedAfterBlock_ShouldReturnSameBlockedOutcome()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        (AssetVersion version, AssetDraftWorkspace workspaceEntity) = await SeedVersionWorkspaceAsync(db, asset);
        await db.SaveChangesAsync();
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        var workspace = new AssetDraftWorkspaceSnapshot(
            workspaceEntity.Id,
            workspaceEntity.AssetId,
            workspaceEntity.AssetVersionId,
            workspaceEntity.WorkspaceVersionScopeKey,
            workspaceEntity.WorkspaceRevision,
            workspaceEntity.CaseRevision,
            workspaceEntity.MaterialMetadataHeadRevision,
            workspaceEntity.SourceDeclarationHeadRevision,
            workspaceEntity.SellerEvidenceHeadRevision);
        var operationId = Guid.NewGuid();
        const string digest = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var request = new GuardedSubmissionRequest(
            author.Id,
            asset.Id,
            version.Id,
            workspace.WorkspaceId,
            workspace.WorkspaceRevision,
            workspace.CaseRevision,
            operationId,
            digest,
            null);

        GuardedSubmissionResult first = await store.AttemptGuardedSubmission(request, CancellationToken.None);
        await db.AssetDraftWorkspaces.Where(w => w.Id == workspaceEntity.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.WorkspaceRevision, w => w.WorkspaceRevision + 1));
        AssetVersion nextVersion = TestData.CreateAssetVersion(asset.Id, versionNumber: 2, isCurrent: false);
        db.AssetVersions.Add(nextVersion);
        await db.SaveChangesAsync();
        await db.AssetDraftWorkspaces.Where(w => w.Id == workspaceEntity.Id).ExecuteUpdateAsync(s => s
            .SetProperty(w => w.AssetVersionId, nextVersion.Id).SetProperty(w => w.WorkspaceVersionScopeKey, nextVersion.Id));
        db.ChangeTracker.Clear();
        GuardedSubmissionResult second = await store.AttemptGuardedSubmission(request, CancellationToken.None);

        first.Status.Should().Be(GuardedSubmissionStatus.BLOCKED);
        second.Status.Should().Be(GuardedSubmissionStatus.BLOCKED);
        second.BlockedReasonCode.Should().Be(first.BlockedReasonCode);
        (await db.JsonMutationIdempotencyRecords.CountAsync()).Should().Be(1);
        (await db.ModerationSubmissions.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task AttemptGuardedSubmission_WhenSameOperationIdWithDifferentDigest_ShouldConflict()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        (AssetVersion version, AssetDraftWorkspace workspaceEntity) = await SeedVersionWorkspaceAsync(db, asset);
        await db.SaveChangesAsync();
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        var operationId = Guid.NewGuid();
        var firstRequest = new GuardedSubmissionRequest(
            author.Id,
            asset.Id,
            version.Id,
            workspaceEntity.Id,
            workspaceEntity.WorkspaceRevision,
            workspaceEntity.CaseRevision,
            operationId,
            "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd",
            null);
        await store.AttemptGuardedSubmission(firstRequest, CancellationToken.None);

        GuardedSubmissionResult conflict = await store.AttemptGuardedSubmission(
            firstRequest with { RequestDigest = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" },
            CancellationToken.None);

        conflict.Status.Should().Be(GuardedSubmissionStatus.CONFLICT);
        (await db.JsonMutationIdempotencyRecords.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task AttemptGuardedSubmission_WhenActorIsNotAssetAuthor_ShouldBlockWithoutIdempotencyRecord()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User _, Category _, Asset asset) = await SeedAssetAsync(db);
        User stranger = TestData.CreateUser("stranger", "stranger@example.test");
        db.Users.Add(stranger);
        await db.SaveChangesAsync();

        (AssetVersion version, AssetDraftWorkspace workspaceEntity) = await SeedVersionWorkspaceAsync(db, asset);
        await db.SaveChangesAsync();
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));

        GuardedSubmissionResult result = await store.AttemptGuardedSubmission(
            new GuardedSubmissionRequest(
                stranger.Id,
                asset.Id,
                version.Id,
                workspaceEntity.Id,
                workspaceEntity.WorkspaceRevision,
                workspaceEntity.CaseRevision,
                Guid.NewGuid(),
                "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                null),
            CancellationToken.None);

        result.Status.Should().Be(GuardedSubmissionStatus.BLOCKED);
        result.BlockedReasonCode.Should().Be(ErrorCodes.ERR_FORBIDDEN);
        (await db.JsonMutationIdempotencyRecords.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task GetCaseSummaryForModerator_WhenModeratorOwnsAsset_ShouldDenyAccess()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        author.Role = AppRoles.MODERATOR;
        await db.SaveChangesAsync();

        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        AssetDraftWorkspace workspace = CreateVersionWorkspace(asset.Id, version.Id);
        db.AssetVersions.Add(version);
        db.AssetDraftWorkspaces.Add(workspace);
        ModerationSubmission submission = CreateSubmission(asset, version, workspace, author);
        db.ModerationSubmissions.Add(submission);
        await db.SaveChangesAsync();

        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        ModerationCaseAccessResult access = await store.GetCaseSummaryForModerator(author.Id, submission.Id);

        access.Status.Should().Be(ModerationCaseAccessStatus.SELF_OWNED_DENIED);
    }

    [Fact]
    public async Task GetCaseSummaryForModerator_WhenCandidatesHaveNullableRanks_ShouldOrderByPathThenChannelRanksThenKey()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        User moderator = TestData.CreateUser("ordering-reviewer", "ordering-reviewer@example.test");
        moderator.Role = AppRoles.MODERATOR;
        db.Users.Add(moderator);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        AssetDraftWorkspace workspace = CreateVersionWorkspace(asset.Id, version.Id);
        db.AssetVersions.Add(version);
        db.AssetDraftWorkspaces.Add(workspace);
        var report = new CodeAnalysisReportHeader
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            PolicyVersion = ReviewAnalysisPolicy.KEY,
            InputRevision = 1,
            Purpose = CodeAnalysisReportPurpose.FIXTURE,
            ReviewEligible = false,
            IsFinalized = true,
            ReportSchemaVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.CodeAnalysisReportHeaders.Add(report);
        ModerationSubmission submission = CreateSubmission(asset, version, workspace, author, reportId: report.Id);
        db.ModerationSubmissions.Add(submission);
        (string Key, string Path, int? Token, int? Semantic)[] input =
        [
            ("a-no-ranks", "own.py", null, null), ("a-token-second", "own.py", 2, 1),
            ("z-sem-second", "own.py", null, 2), ("z-token-first", "own.py", 1, 5),
            ("b-sem-first", "own.py", null, 1), ("z-no-ranks", "own.py", null, null),
            ("path-first", "a.py", null, null)
        ];
        foreach ((string Key, string Path, int? Token, int? Semantic) row in input)
        {
            db.CodeAnalysisReportFindings.Add(new CodeAnalysisReportFinding
            {
                Id = Guid.NewGuid(),
                ReportId = report.Id,
                FindingKey = row.Key,
                FindingType = "SIMILARITY_SEMANTIC",
                OwnScopePath = row.Path,
                OwnExcerpt = "own",
                ForeignSensitivity = CodeAnalysisFindingSensitivity.PRIVATE,
                TokenRank = row.Token,
                SemanticRank = row.Semantic,
                ReasonText = "Private comparison.",
                RequestedAction = "Wait.",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync();
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db), auditWriter: TestData.CreateReviewAuditWriter(db));
        ModerationCaseAccessResult result = await store.GetCaseSummaryForModerator(moderator.Id, submission.Id);
        result.Summary!.Kind.Should().Be(ModerationSubmissionKind.STANDARD);
        result.Summary!.RetrievalEvidence!.Candidates.Select(c => c.FindingKey).Should().Equal(
            "path-first", "z-token-first", "a-token-second", "b-sem-first", "z-sem-second", "a-no-ranks", "z-no-ranks");
    }

    [Fact]
    public async Task GetCaseSummaryForModerator_WhenReportHasNoticeFinding_ShouldKeepSimilarityScoresAvailableAndListNoticeType()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, _, Asset asset) = await SeedAssetAsync(db);
        User moderator = TestData.CreateUser("notice-reviewer", "notice-reviewer@example.test");
        moderator.Role = AppRoles.MODERATOR;
        db.Users.Add(moderator);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        AssetDraftWorkspace workspace = CreateVersionWorkspace(asset.Id, version.Id);
        db.AssetVersions.Add(version);
        db.AssetDraftWorkspaces.Add(workspace);
        CodeAnalysisReportHeader report = CreateReportHeader(asset.Id, version.Id, version.ContentSha256);
        string queryId = new('a', 64);
        RetrievalReportSummary retrieval = new(20, 20, 40,
            [new RetrievalQuerySummary(queryId, 1, 1, 0, 0, 0, 0, 1, 0, 0, 0, null)]);
        report.RetrievalSummaryJson = System.Text.Json.JsonSerializer.Serialize(
            new PersistedRetrievalSummary("snapshot", "revision", 0, retrieval),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        db.CodeAnalysisReportHeaders.Add(report);
        ModerationSubmission submission = CreateSubmission(asset, version, workspace, author, reportId: report.Id);
        db.ModerationSubmissions.Add(submission);
        CodeAnalysisReportFinding similarity = CreateReviewFinding(report.Id, Guid.NewGuid(), "similarity",
            System.Text.Json.JsonSerializer.Serialize(new RetrievalCandidateEvidence(queryId, "FILE", "TOKEN",
                1, 0.9, null, null, true), new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)),
            "SIMILARITY_TOKEN");
        CodeAnalysisReportFinding notice = CreateReviewFinding(report.Id, Guid.NewGuid(), "notice", null, "NOTICE_MISSING");
        db.CodeAnalysisReportFindings.AddRange(similarity, notice);
        await db.SaveChangesAsync();

        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db),
            auditWriter: TestData.CreateReviewAuditWriter(db));
        ModerationCaseAccessResult result = await store.GetCaseSummaryForModerator(moderator.Id, submission.Id);

        result.Summary!.RetrievalEvidence!.ScoresAvailability.Should().Be("AVAILABLE");
        result.Summary.RetrievalEvidence.Candidates.Should().ContainSingle(c => c.FindingKey == "notice");
        result.Summary.RetrievalEvidence.Candidates.Single(c => c.FindingKey == "notice").Scores.Should().BeNull();
    }

    [Fact]
    public async Task GetCaseSummaryForModerator_WhenReadingFrozenReview_ShouldReturnDecisionBasisFindingIdsResponsesAndPlanReference()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, _, Asset asset) = await SeedAssetAsync(db);
        User moderator = TestData.CreateUser("case-projection-reviewer", "case-projection-reviewer@example.test");
        moderator.Role = AppRoles.MODERATOR;
        db.Users.Add(moderator);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        AssetDraftWorkspace workspace = CreateVersionWorkspace(asset.Id, version.Id);
        workspace.SellerEvidenceHeadRevision = 1;
        db.AssetVersions.Add(version);
        db.AssetDraftWorkspaces.Add(workspace);
        CodeAnalysisReportHeader report = CreateReportHeader(asset.Id, version.Id, version.ContentSha256);
        report.FindingCount = 2;
        db.CodeAnalysisReportHeaders.Add(report);

        var requiredAnsweredFindingId = Guid.NewGuid();
        var requiredOutstandingFindingId = Guid.NewGuid();
        db.CodeAnalysisReportFindings.AddRange(
            CreateReviewFinding(report.Id, requiredAnsweredFindingId, "answered", "{\"material\":true}"),
            CreateReviewFinding(report.Id, requiredOutstandingFindingId, "outstanding", "{\"material\":true}", "POTENTIAL_MATCH"));
        var sellerResponseId = Guid.NewGuid();
        DateTimeOffset responseCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        db.SellerResponses.Add(new SellerResponse
        {
            Id = sellerResponseId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            ReportId = report.Id,
            FindingId = requiredAnsweredFindingId,
            SellerEvidenceRevision = 1,
            ResponseType = SellerResponseType.PRIOR_WORK,
            Text = "Bounded seller answer.",
            ActorUserId = author.Id,
            CreatedAt = responseCreatedAt
        });
        var sellerSnapshot = System.Text.Json.JsonSerializer.Serialize(
            new SellerEvidenceSnapshot(report.Id, [sellerResponseId], "Private seller evidence."),
            SellerResponseRequirements.JsonOptions);
        db.AssetSellerEvidenceRevisions.Add(new AssetSellerEvidenceRevision
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            Revision = 1,
            AuthorUserId = author.Id,
            ContentDigest = new string('c', 64),
            SchemaVersion = 1,
            PayloadJson = sellerSnapshot,
            CreatedAt = DateTimeOffset.UtcNow
        });

        var coverageRequestId = Guid.NewGuid();
        const string tupleDigest = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        db.CoverageReviewRequests.Add(new CoverageReviewRequest
        {
            Id = coverageRequestId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            ReportId = report.Id,
            OwnerUserId = author.Id,
            ContentSha256 = version.ContentSha256,
            DeclarationRevision = 1,
            MaterialRevision = 1,
            SellerEvidenceRevision = 1,
            PolicyVersion = report.PolicyVersion,
            PolicyDigest = new string('b', 64),
            TupleDigest = tupleDigest,
            RequestedWorkspaceRevision = workspace.WorkspaceRevision,
            Revision = 1,
            IsOpen = false,
            CreatedAt = DateTimeOffset.UtcNow
        });
        const string expectedPlanReference = "manual-review/complete-omitted-files";
        var coverageAssessmentId = Guid.NewGuid();
        db.CoverageAssessments.Add(new CoverageAssessment
        {
            Id = coverageAssessmentId,
            RequestId = coverageRequestId,
            ModeratorUserId = moderator.Id,
            Outcome = CoverageAssessmentOutcome.ACCEPTED,
            CompetenceJson = "[\"Archive review\"]",
            ScopeJson = "[]",
            ManageableScope = true,
            ReviewPlan = "Review all omitted files.",
            CompletedPlanReference = expectedPlanReference,
            Rationale = "The stored evidence was readable.",
            CreatedAt = DateTimeOffset.UtcNow
        });
        ModerationSubmission submission = CreateSubmission(asset, version, workspace, author, reportId: report.Id,
            coverageAssessmentId: coverageAssessmentId, sellerEvidenceRevision: 1);
        submission.State = ModerationSubmissionState.IN_REVIEW;
        submission.ClaimHolderId = moderator.Id;
        submission.ClaimedAt = DateTimeOffset.UtcNow;
        db.ModerationSubmissions.Add(submission);
        await db.SaveChangesAsync();

        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db),
            auditWriter: TestData.CreateReviewAuditWriter(db));
        ModerationCaseAccessResult result = await store.GetCaseSummaryForModerator(moderator.Id, submission.Id);
        ModerationCaseSummary summary = result.Summary!;
        SellerResponseRequirements.Summary decisionRequirements =
            (await SellerResponseRequirements.ReadSummaries(db,
                [new(workspace.Id, report.Id, workspace.Id, version.Id, submission.SellerEvidenceRevision)],
                CancellationToken.None))[workspace.Id];

        result.Status.Should().Be(ModerationCaseAccessStatus.FOUND);
        summary.Basis.Should().Be(new DecisionBasis(version.Id, submission.ContentSha256, report.Id,
            submission.DeclarationRevision, submission.MaterialMetadataRevision, submission.PolicyVersion,
            submission.SellerEvidenceRevision, submission.CaseRevision));
        summary.ManualPlanCompletionReferenceRequired.Should().BeTrue();
        var summaryJson = System.Text.Json.JsonSerializer.Serialize(summary,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        summaryJson.Should().NotContain("expectedManualPlanCompletionReference").And.NotContain(expectedPlanReference);
        summaryJson.Should().Contain("\"isClaimedByCaller\":true");
        summary.RetrievalEvidence!.ReportId.Should().Be(report.Id);
        summary.RetrievalEvidence.ResponseRequirementsAvailable.Should().BeTrue();
        ModeratorRetrievalCandidate answered = summary.RetrievalEvidence.Candidates.Single(c => c.FindingId == requiredAnsweredFindingId);
        ModeratorRetrievalCandidate outstanding = summary.RetrievalEvidence.Candidates.Single(c => c.FindingId == requiredOutstandingFindingId);
        answered.ResponseRequired.Should().Be(decisionRequirements.Required.Contains(requiredAnsweredFindingId));
        answered.Answered.Should().Be(decisionRequirements.Answered.Contains(requiredAnsweredFindingId));
        outstanding.ResponseRequired.Should().Be(decisionRequirements.Required.Contains(requiredOutstandingFindingId));
        outstanding.Answered.Should().Be(decisionRequirements.Answered.Contains(requiredOutstandingFindingId));
        ModeratorSellerResponse projectedResponse = answered.Responses.Should().ContainSingle().Which;
        projectedResponse.Kind.Should().Be(SellerResponseType.PRIOR_WORK);
        projectedResponse.Text.Should().Be("Bounded seller answer.");
        projectedResponse.CreatedAt.Should().BeCloseTo(responseCreatedAt, TimeSpan.FromMilliseconds(1));
        outstanding.Responses.Should().BeEmpty();
        summary.RetrievalEvidence.Candidates.Select(c => c.FindingId).Should().OnlyHaveUniqueItems();
    }

    private static CodeAnalysisReportFinding CreateReviewFinding(Guid reportId, Guid id, string key, string? retrievalEvidenceJson, string findingType = "SIMILARITY_SEMANTIC") => new()
    {
        Id = id,
        ReportId = reportId,
        FindingKey = key,
        FindingType = findingType,
        OwnScopePath = "src/review.py",
        OwnExcerpt = "private excerpt",
        ForeignSensitivity = CodeAnalysisFindingSensitivity.PRIVATE,
        ReasonText = "private reason",
        RequestedAction = "private requested action",
        RetrievalEvidenceJson = retrievalEvidenceJson,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task AttemptGuardedSubmission_WhenWorkspaceVersionMismatchesRequest_ShouldConflictWithoutIdempotency()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        AssetDraftWorkspaceSnapshot preUpload = await new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db))
            .EnsurePreUploadWorkspace(asset.Id, author.Id);
        (AssetVersion version, _) = await SeedVersionWorkspaceAsync(db, asset);
        await db.SaveChangesAsync();

        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        GuardedSubmissionResult result = await store.AttemptGuardedSubmission(
            new GuardedSubmissionRequest(
                author.Id,
                asset.Id,
                version.Id,
                preUpload.WorkspaceId,
                preUpload.WorkspaceRevision,
                preUpload.CaseRevision,
                Guid.NewGuid(),
                "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff",
                null),
            CancellationToken.None);

        result.Status.Should().Be(GuardedSubmissionStatus.CONFLICT);
        (await db.JsonMutationIdempotencyRecords.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Schema_WhenSubmissionWorkspaceVersionMismatches_ShouldRejectInsert()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        (AssetVersion version1, AssetVersion version2) = CreateTwoVersions(asset.Id);
        AssetDraftWorkspace workspaceV2 = CreateVersionWorkspace(asset.Id, version2.Id);
        db.AssetVersions.AddRange(version1, version2);
        db.AssetDraftWorkspaces.Add(workspaceV2);
        await db.SaveChangesAsync();

        db.ModerationSubmissions.Add(CreateSubmission(
            asset,
            version1,
            workspaceV2,
            author,
            workspaceVersionScopeKey: version1.Id));
        Func<Task> act = () => db.SaveChangesAsync();
        DbUpdateException exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Schema_WhenPublicationSubmissionVersionMismatches_ShouldRejectInsert()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        (AssetVersion version1, AssetVersion version2) = CreateTwoVersions(asset.Id);
        AssetDraftWorkspace workspaceV1 = CreateVersionWorkspace(asset.Id, version1.Id);
        db.AssetVersions.AddRange(version1, version2);
        db.AssetDraftWorkspaces.Add(workspaceV1);
        ModerationSubmission submission = CreateSubmission(asset, version1, workspaceV1, author);
        CodeAnalysisReportHeader report = CreateReportHeader(asset.Id, version2.Id, version2.ContentSha256);
        db.ModerationSubmissions.Add(submission);
        db.CodeAnalysisReportHeaders.Add(report);
        await db.SaveChangesAsync();

        db.PublicationSnapshots.Add(new PublicationSnapshot
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version2.Id,
            ContentSha256 = version2.ContentSha256,
            CodeAnalysisReportHeaderId = report.Id,
            ModerationSubmissionId = submission.Id,
            PolicyVersion = "policy-v1",
            ApprovedMetadataJson = "{}",
            RightsReferenceJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        Func<Task> act = () => db.SaveChangesAsync();
        DbUpdateException exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Schema_WhenRevisionWorkspaceVersionMismatches_ShouldRejectInsert()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        (AssetVersion version1, AssetVersion version2) = CreateTwoVersions(asset.Id);
        AssetDraftWorkspace workspaceV2 = CreateVersionWorkspace(asset.Id, version2.Id);
        db.AssetVersions.AddRange(version1, version2);
        db.AssetDraftWorkspaces.Add(workspaceV2);
        await db.SaveChangesAsync();

        db.AssetMaterialMetadataRevisions.Add(new AssetMaterialMetadataRevision
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version1.Id,
            WorkspaceVersionScopeKey = version1.Id,
            WorkspaceId = workspaceV2.Id,
            Revision = 1,
            AuthorUserId = author.Id,
            ContentDigest = new string('a', 64),
            SchemaVersion = 1,
            PayloadJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        Func<Task> act = () => db.SaveChangesAsync();
        DbUpdateException exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Schema_WhenPreUploadRevisionMatchesPreUploadWorkspace_ShouldAllowInsert()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        AssetDraftWorkspaceSnapshot preUpload = await new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db))
            .EnsurePreUploadWorkspace(asset.Id, author.Id);
        db.AssetSourceDeclarationRevisions.Add(new AssetSourceDeclarationRevision
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = null,
            WorkspaceVersionScopeKey = PreUpload,
            WorkspaceId = preUpload.WorkspaceId,
            Revision = 1,
            AuthorUserId = author.Id,
            ContentDigest = new string('b', 64),
            SchemaVersion = 1,
            PayloadJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        (await db.AssetSourceDeclarationRevisions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Schema_WhenVersionScopedRevisionMatchesWorkspace_ShouldAllowInsert()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        (AssetVersion version, AssetDraftWorkspace workspace) = await SeedVersionWorkspaceAsync(db, asset);
        db.AssetSellerEvidenceRevisions.Add(new AssetSellerEvidenceRevision
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceVersionScopeKey = version.Id,
            WorkspaceId = workspace.Id,
            Revision = 1,
            AuthorUserId = author.Id,
            ContentDigest = new string('c', 64),
            SchemaVersion = 1,
            PayloadJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        (await db.AssetSellerEvidenceRevisions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Schema_WhenPreUploadRevisionTargetsVersionWorkspace_ShouldRejectInsert()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        (AssetVersion _, AssetDraftWorkspace versionWorkspace) = await SeedVersionWorkspaceAsync(db, asset);
        await db.SaveChangesAsync();

        db.AssetSourceDeclarationRevisions.Add(new AssetSourceDeclarationRevision
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = null,
            WorkspaceVersionScopeKey = PreUpload,
            WorkspaceId = versionWorkspace.Id,
            Revision = 1,
            AuthorUserId = author.Id,
            ContentDigest = new string('e', 64),
            SchemaVersion = 1,
            PayloadJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        Func<Task> act = () => db.SaveChangesAsync();
        DbUpdateException exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Schema_WhenVersionScopedRevisionTargetsPreUploadWorkspace_ShouldRejectInsert()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        AssetDraftWorkspaceSnapshot preUpload = await new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db))
            .EnsurePreUploadWorkspace(asset.Id, author.Id);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        db.AssetMaterialMetadataRevisions.Add(new AssetMaterialMetadataRevision
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceVersionScopeKey = version.Id,
            WorkspaceId = preUpload.WorkspaceId,
            Revision = 1,
            AuthorUserId = author.Id,
            ContentDigest = new string('f', 64),
            SchemaVersion = 1,
            PayloadJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        Func<Task> act = () => db.SaveChangesAsync();
        DbUpdateException exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Schema_WhenRevisionScopeColumnsMismatchNullability_ShouldRejectInsert(bool versionIdOnAssetVersionColumn)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        AssetDraftWorkspaceSnapshot preUpload = await new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db))
            .EnsurePreUploadWorkspace(asset.Id, author.Id);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        db.AssetSellerEvidenceRevisions.Add(new AssetSellerEvidenceRevision
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = versionIdOnAssetVersionColumn ? version.Id : null,
            WorkspaceVersionScopeKey = versionIdOnAssetVersionColumn ? PreUpload : version.Id,
            WorkspaceId = preUpload.WorkspaceId,
            Revision = 1,
            AuthorUserId = author.Id,
            ContentDigest = new string('g', 64),
            SchemaVersion = 1,
            PayloadJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        });

        Func<Task> act = () => db.SaveChangesAsync();
        DbUpdateException exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Schema_WhenCrossAssetCurrentPublicationSnapshotPointer_ShouldRejectUpdate()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User _, Category category, Asset asset1) = await SeedAssetAsync(db);
        User author2 = TestData.CreateUser("author2", "author2@example.test");
        Asset asset2 = TestData.CreateAsset(author2.Id, category.Id);
        db.Users.Add(author2);
        db.Assets.Add(asset2);
        PublicationSnapshot snapshotOnAsset2 = await SeedPublicationSnapshotAsync(db, asset2, author2);
        await db.SaveChangesAsync();

        asset1.CurrentPublicationSnapshotId = snapshotOnAsset2.Id;
        Func<Task> act = () => db.SaveChangesAsync();
        DbUpdateException exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task Schema_WhenSubmissionWorkspaceBelongsToAnotherAsset_ShouldRejectInsert()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author1, Category category, Asset asset1) = await SeedAssetAsync(db);
        User author2 = TestData.CreateUser("author2", "author2@example.test");
        Asset asset2 = TestData.CreateAsset(author2.Id, category.Id);
        db.Users.Add(author2);
        db.Assets.Add(asset2);
        AssetVersion version1 = TestData.CreateAssetVersion(asset1.Id);
        AssetVersion version2 = TestData.CreateAssetVersion(asset2.Id);
        AssetDraftWorkspace workspace2 = CreateVersionWorkspace(asset2.Id, version2.Id);
        db.AssetVersions.AddRange(version1, version2);
        db.AssetDraftWorkspaces.Add(workspace2);
        await db.SaveChangesAsync();

        db.ModerationSubmissions.Add(CreateSubmission(
            asset1,
            version1,
            workspace2,
            author1,
            workspaceVersionScopeKey: version1.Id));
        Func<Task> act = () => db.SaveChangesAsync();
        DbUpdateException exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task AttemptGuardedSubmission_WhenNoTrustedProductionReport_ShouldBlock()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        (AssetVersion version, AssetDraftWorkspace workspaceEntity) = await SeedVersionWorkspaceAsync(db, asset);
        await db.SaveChangesAsync();
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));

        GuardedSubmissionResult result = await store.AttemptGuardedSubmission(
            new GuardedSubmissionRequest(
                author.Id,
                asset.Id,
                version.Id,
                workspaceEntity.Id,
                workspaceEntity.WorkspaceRevision,
                workspaceEntity.CaseRevision,
                Guid.NewGuid(),
                "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                null),
            CancellationToken.None);

        result.Status.Should().Be(GuardedSubmissionStatus.BLOCKED);
        result.BlockedReasonCode.Should().Be(ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE);
    }

    [Fact]
    public async Task Schema_WhenDuplicateActiveSubmission_ShouldRejectSecondInsert()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        AssetDraftWorkspace workspace = CreateVersionWorkspace(asset.Id, version.Id);
        db.AssetVersions.Add(version);
        db.AssetDraftWorkspaces.Add(workspace);
        await db.SaveChangesAsync();

        db.ModerationSubmissions.Add(CreateSubmission(asset, version, workspace, author));
        await db.SaveChangesAsync();

        db.ModerationSubmissions.Add(CreateSubmission(asset, version, workspace, author, Guid.NewGuid()));
        Func<Task> act = () => db.SaveChangesAsync();
        DbUpdateException exception = (await act.Should().ThrowAsync<DbUpdateException>()).Which;
        exception.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task RevocationHoldsUserLock_ProbeWaitsAndSeesRevokedRoleWithoutWorkspaceEffects()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        User moderator = TestData.CreateUser("mod", "mod@example.test");
        moderator.Role = AppRoles.MODERATOR;
        db.Users.Add(moderator);
        await db.SaveChangesAsync();

        AssetDraftWorkspaceSnapshot workspace = await new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db))
            .EnsurePreUploadWorkspace(asset.Id, author.Id);
        var beforeRevision = workspace.WorkspaceRevision;

        using ManualResetEventSlim revokeHoldsUserLock = new(false);
        using ManualResetEventSlim probeLockAttemptStarted = new(false);
        var syncTimeout = TimeSpan.FromSeconds(10);

        var revokeTask = Task.Run(async () =>
        {
            await using ApplicationDbContext revokeDb = fixture.CreateDbContext();
            await using IDbContextTransaction revokeTx = await revokeDb.Database.BeginTransactionAsync();
            var revokeStore = new UserStore(revokeDb);
            UserPersistedRole? locked = await revokeStore.LockAndReadPersistedRole(moderator.Id);
            revokeHoldsUserLock.Set();
            probeLockAttemptStarted.Wait(syncTimeout).Should().BeTrue();
            await Task.Delay(100);
            await revokeStore.TryAssignRole(moderator.Id, AppRoles.USER, locked!.RoleRevision);
            await revokeTx.CommitAsync();
        });

        Task<UserPersistedRole?> probeTask = Task.Run(async () =>
        {
            revokeHoldsUserLock.Wait(syncTimeout).Should().BeTrue();
            await using ApplicationDbContext probeDb = fixture.CreateDbContext();
            await using IDbContextTransaction probeTx = await probeDb.Database.BeginTransactionAsync();
            var probeStore = new ModerationFoundationStore(probeDb, new AssetStore(probeDb), new UserStore(probeDb), new EfUnitOfWork(probeDb));
            AssetDraftWorkspaceSnapshot? lockedWorkspace = await probeStore.LockAssetAndWorkspaceForUpdate(
                asset.Id,
                workspace.WorkspaceId,
                beforeRevision);
            lockedWorkspace.Should().NotBeNull();
            probeLockAttemptStarted.Set();
            UserPersistedRole? liveRole = await probeStore.LockRoleAfterWorkspace(moderator.Id);
            await probeTx.RollbackAsync();
            return liveRole;
        });

        await Task.WhenAll(revokeTask, probeTask);
        (await probeTask)!.Role.Should().Be(AppRoles.USER);

        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        AssetDraftWorkspace reloaded = await verifyDb.AssetDraftWorkspaces.FirstAsync(w => w.Id == workspace.WorkspaceId);
        reloaded.WorkspaceRevision.Should().Be(beforeRevision);
    }

    [Fact]
    public async Task SaveDraftRevision_WhenSubmissionRowIsConcurrentlyLocked_ShouldWaitWithoutDeadlockAndSucceed()
    {
        // Save (workspace -> submission) and withdrawal (asset -> workspace -> submission) share one
        // lock order; a submission row held by another transaction must only delay the save.
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        (AssetVersion version, AssetDraftWorkspace workspace) = await SeedVersionWorkspaceAsync(db, asset);
        ModerationSubmission submission = CreateSubmission(asset, version, workspace, author);
        db.ModerationSubmissions.Add(submission);
        await db.SaveChangesAsync();

        var material = new AssetBlock.Domain.Core.Dto.Assets.SellerDraftMaterialPayload(
            "T", null, asset.CategoryId, ["tools"]);
        var payloadJson = AssetBlock.Application.Common.DraftPayloadJson.Serialize(material);
        var contentDigest = AssetBlock.Application.Common.DraftPayloadJson.ComputeDigest(payloadJson);
        var requestDigest = AssetBlock.Application.Common.DraftPayloadJson.ComputeRequestDigest(
            asset.Id, version.Id, ModerationOperationKinds.DRAFT_SAVE, workspace.WorkspaceRevision, payloadJson);

        using ManualResetEventSlim holderHoldsSubmissionLock = new(false);
        var syncTimeout = TimeSpan.FromSeconds(10);

        var holderTask = Task.Run(async () =>
        {
            await using ApplicationDbContext holderDb = fixture.CreateDbContext();
            await using IDbContextTransaction holderTx = await holderDb.Database.BeginTransactionAsync();
            await holderDb.Database.ExecuteSqlAsync(
                $"SELECT \"Id\" FROM moderation_submissions WHERE \"Id\" = {submission.Id} FOR UPDATE");
            holderHoldsSubmissionLock.Set();
            // Give the save attempt time to reach the submission lock and block.
            await Task.Delay(300);
            await holderTx.CommitAsync();
        });

        Task<ModerationDraftSaveResult> saveTask = Task.Run(async () =>
        {
            holderHoldsSubmissionLock.Wait(syncTimeout).Should().BeTrue();
            await using ApplicationDbContext saveDb = fixture.CreateDbContext();
            var saveStore = new ModerationFoundationStore(saveDb, new AssetStore(saveDb), new UserStore(saveDb), new EfUnitOfWork(saveDb));
            return await saveStore.SaveDraftRevision(
                new DraftRevisionSaveRequest(
                    author.Id,
                    asset.Id,
                    version.Id,
                    ModerationOperationKinds.DRAFT_SAVE,
                    Guid.NewGuid(),
                    requestDigest,
                    payloadJson,
                    AssetBlock.Domain.Core.Constants.SellerDraftLimits.MATERIAL_METADATA_SCHEMA_VERSION,
                    contentDigest,
                    workspace.WorkspaceRevision),
                CancellationToken.None);
        });

        await Task.WhenAll(holderTask, saveTask);
        (await saveTask).Status.Should().Be(ModerationDraftSaveStatus.SUCCEEDED);
    }

    [Fact]
    public async Task SaveDraftRevision_WhenAssetIsSoftDeletedBeforeLock_ShouldRejectWithoutSideEffects()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        AssetDraftWorkspaceSnapshot workspace = await store.EnsurePreUploadWorkspace(asset.Id, author.Id);

        var material = new AssetBlock.Domain.Core.Dto.Assets.SellerDraftMaterialPayload(
            "T", null, asset.CategoryId, []);
        var payloadJson = AssetBlock.Application.Common.DraftPayloadJson.Serialize(material);
        var contentDigest = AssetBlock.Application.Common.DraftPayloadJson.ComputeDigest(payloadJson);
        var requestDigest = AssetBlock.Application.Common.DraftPayloadJson.ComputeRequestDigest(
            asset.Id, null, ModerationOperationKinds.DRAFT_SAVE, workspace.WorkspaceRevision, payloadJson);
        var operationId = Guid.NewGuid();

        using ManualResetEventSlim deleteHoldsAssetLock = new(false);
        var syncTimeout = TimeSpan.FromSeconds(10);

        var deleteTask = Task.Run(async () =>
        {
            await using ApplicationDbContext deleteDb = fixture.CreateDbContext();
            await using IDbContextTransaction deleteTx = await deleteDb.Database.BeginTransactionAsync();
            await deleteDb.Database.ExecuteSqlAsync(
                $"UPDATE assets SET \"DeletedAt\" = NOW() WHERE \"Id\" = {asset.Id}");
            deleteHoldsAssetLock.Set();
            // Let the save block on the asset row lock before committing the delete.
            await Task.Delay(300);
            await deleteTx.CommitAsync();
        });

        Task<ModerationDraftSaveResult> saveTask = Task.Run(async () =>
        {
            deleteHoldsAssetLock.Wait(syncTimeout).Should().BeTrue();
            await using ApplicationDbContext saveDb = fixture.CreateDbContext();
            var saveStore = new ModerationFoundationStore(saveDb, new AssetStore(saveDb), new UserStore(saveDb), new EfUnitOfWork(saveDb));
            return await saveStore.SaveDraftRevision(
                new DraftRevisionSaveRequest(
                    author.Id,
                    asset.Id,
                    null,
                    ModerationOperationKinds.DRAFT_SAVE,
                    operationId,
                    requestDigest,
                    payloadJson,
                    AssetBlock.Domain.Core.Constants.SellerDraftLimits.MATERIAL_METADATA_SCHEMA_VERSION,
                    contentDigest,
                    workspace.WorkspaceRevision),
                CancellationToken.None);
        });

        await Task.WhenAll(deleteTask, saveTask);
        ModerationDraftSaveResult save = await saveTask;
        save.Status.Should().BeOneOf(ModerationDraftSaveStatus.FORBIDDEN, ModerationDraftSaveStatus.NOT_FOUND);
        (await db.AssetMaterialMetadataRevisions.CountAsync(r => r.AssetId == asset.Id)).Should().Be(0);
        (await db.JsonMutationIdempotencyRecords.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task SaveDraftRevision_WhenSameOperationIdTargetsDifferentAsset_ShouldNotReplayForeignReceipt()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset assetA) = await SeedAssetAsync(db);
        User authorB = TestData.CreateUser("author-b", "b@example.test");
        db.Users.Add(authorB);
        Category categoryB = TestData.CreateCategory("cat-b", "cat-b");
        db.Categories.Add(categoryB);
        Asset assetB = TestData.CreateAsset(authorB.Id, categoryB.Id);
        db.Assets.Add(assetB);
        await db.SaveChangesAsync();

        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        AssetDraftWorkspaceSnapshot workspaceA = await store.EnsurePreUploadWorkspace(assetA.Id, author.Id);
        AssetDraftWorkspaceSnapshot workspaceB = await store.EnsurePreUploadWorkspace(assetB.Id, authorB.Id);
        var operationId = Guid.NewGuid();

        DraftRevisionSaveRequest firstRequest = MakeScopedSaveRequest(author.Id, assetA.Id, null, operationId, workspaceA.WorkspaceRevision, "Title A");
        DraftRevisionSaveRequest secondRequest = MakeScopedSaveRequest(authorB.Id, assetB.Id, null, operationId, workspaceB.WorkspaceRevision, "Title A");
        ModerationDraftSaveResult first = await store.SaveDraftRevision(firstRequest, CancellationToken.None);
        // Same actor, same operation kind, same operationId, identical payload - but a different asset scope.
        ModerationDraftSaveResult second = await store.SaveDraftRevision(secondRequest, CancellationToken.None);
        // Exact replay of the second request: served from the receipt even though the CAS revision moved.
        ModerationDraftSaveResult replay = await store.SaveDraftRevision(secondRequest, CancellationToken.None);

        first.Status.Should().Be(ModerationDraftSaveStatus.SUCCEEDED);
        first.Replayed.Should().BeFalse();
        second.Status.Should().Be(ModerationDraftSaveStatus.SUCCEEDED);
        second.Replayed.Should().BeFalse();
        replay.Status.Should().Be(ModerationDraftSaveStatus.SUCCEEDED);
        replay.Replayed.Should().BeTrue();
        (await db.JsonMutationIdempotencyRecords.CountAsync()).Should().Be(2);
    }

    private static DraftRevisionSaveRequest MakeScopedSaveRequest(
        Guid actorId,
        Guid assetId,
        Guid? assetVersionId,
        Guid operationId,
        long expectedRevision,
        string title) =>
        new(
            actorId,
            assetId,
            assetVersionId,
            ModerationOperationKinds.DRAFT_SAVE,
            operationId,
            AssetBlock.Application.Common.DraftPayloadJson.ComputeRequestDigest(
                assetId,
                assetVersionId,
                ModerationOperationKinds.DRAFT_SAVE,
                expectedRevision,
                AssetBlock.Application.Common.DraftPayloadJson.Serialize(
                    new AssetBlock.Domain.Core.Dto.Assets.SellerDraftMaterialPayload(title, null, Guid.NewGuid(), []))),
            AssetBlock.Application.Common.DraftPayloadJson.Serialize(
                new AssetBlock.Domain.Core.Dto.Assets.SellerDraftMaterialPayload(title, null, Guid.NewGuid(), [])),
            AssetBlock.Domain.Core.Constants.SellerDraftLimits.MATERIAL_METADATA_SCHEMA_VERSION,
            "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
            expectedRevision);

    [Fact]
    public async Task GetOwnerDeclarationSnapshot_WhenVersionWorkspaceIsMissing_ShouldReturnNullWhilePreUploadScopeWorks()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        await store.EnsurePreUploadWorkspace(asset.Id, author.Id);
        AssetVersion foreignVersion = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(foreignVersion);
        await db.SaveChangesAsync();

        // Unknown/cross-asset version workspace: missing -> null (mapped to 404 upstream).
        SellerDeclarationSnapshotDto? missing =
            await store.GetOwnerDeclarationSnapshot(asset.Id, foreignVersion.Id, author.Id);
        missing.Should().BeNull();

        // Existing pre-upload workspace without a stored declaration: present and empty.
        SellerDeclarationSnapshotDto? empty =
            await store.GetOwnerDeclarationSnapshot(asset.Id, null, author.Id);
        empty.Should().NotBeNull();
        empty!.Declaration.Should().BeNull();
        empty.DeclarationComplete.Should().BeFalse();
        empty.WorkspaceId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task GetOwnerVersionReview_WhenSubmissionIsTerminalAndNotApprovedPublicly_ShouldReportTerminalStateAndIneligibility()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        (AssetVersion version, AssetDraftWorkspace workspace) = await SeedVersionWorkspaceAsync(db, asset);
        ModerationSubmission submission = CreateSubmission(asset, version, workspace, author);
        submission.State = ModerationSubmissionState.WITHDRAWN;
        db.ModerationSubmissions.Add(submission);
        await db.SaveChangesAsync();

        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        SellerVersionReviewDto? review = await store.GetOwnerVersionReview(version.Id, author.Id);

        review.Should().NotBeNull();
        review!.ModerationState.Should().Be(ModerationSubmissionState.WITHDRAWN);
        review.PublicationEligible.Should().BeFalse();
    }

    [Fact]
    public async Task GetOwnerVersionReview_WhenSubmissionHistoryExceedsLimit_ShouldReturnNewestSafeEntriesAndActiveIdentity()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category _, Asset asset) = await SeedAssetAsync(db);
        var moderatorSuffix = Guid.NewGuid().ToString("N");
        User moderator = TestData.CreateUser("moderator-" + moderatorSuffix, "moderator-" + moderatorSuffix + "@example.test");
        db.Users.Add(moderator);
        (AssetVersion version, AssetDraftWorkspace workspace) = await SeedVersionWorkspaceAsync(db, asset);
        DateTimeOffset startValue = DateTimeOffset.UtcNow.AddHours(-1);
        DateTimeOffset start = new(startValue.Ticks - startValue.Ticks % 10, TimeSpan.Zero);
        var submissions = new List<ModerationSubmission>();
        var decisions = new List<ModerationDecisionRecord>();
        for (var index = 0; index < 22; index++)
        {
            ModerationSubmission submission = CreateSubmission(asset, version, workspace, author);
            submission.CreatedAt = start.AddMinutes(index);
            submission.State = ModerationSubmissionState.WITHDRAWN;
            if (index == 3)
            {
                submission.State = ModerationSubmissionState.CHANGES_REQUESTED;
                decisions.Add(new ModerationDecisionRecord
                {
                    Id = Guid.NewGuid(),
                    SubmissionId = submission.Id,
                    AssetId = asset.Id,
                    AssetVersionId = version.Id,
                    ModeratorUserId = moderator.Id,
                    Outcome = ModerationSubmissionState.CHANGES_REQUESTED,
                    CaseRevision = submission.CaseRevision,
                    Message = "Please clarify the source.",
                    CreatedAt = submission.CreatedAt.AddSeconds(1)
                });
            }
            else if (index == 4)
            {
                submission.State = ModerationSubmissionState.REJECTED;
                decisions.Add(new ModerationDecisionRecord
                {
                    Id = Guid.NewGuid(),
                    SubmissionId = submission.Id,
                    AssetId = asset.Id,
                    AssetVersionId = version.Id,
                    ModeratorUserId = moderator.Id,
                    Outcome = ModerationSubmissionState.REJECTED,
                    CaseRevision = submission.CaseRevision,
                    Message = "The rejected basis reason.",
                    CreatedAt = submission.CreatedAt.AddSeconds(1)
                });
            }
            else if (index == 5)
            {
                submission.State = ModerationSubmissionState.APPROVED;
                decisions.Add(new ModerationDecisionRecord
                {
                    Id = Guid.NewGuid(),
                    SubmissionId = submission.Id,
                    AssetId = asset.Id,
                    AssetVersionId = version.Id,
                    ModeratorUserId = moderator.Id,
                    Outcome = ModerationSubmissionState.APPROVED,
                    CaseRevision = submission.CaseRevision,
                    Message = "Private approval text.",
                    CreatedAt = submission.CreatedAt.AddSeconds(1)
                });
            }
            else if (index == 21)
            {
                submission.State = ModerationSubmissionState.IN_REVIEW;
                submission.ClaimHolderId = moderator.Id;
                submission.ClaimedAt = submission.CreatedAt;
            }
            submissions.Add(submission);
        }

        db.ModerationSubmissions.AddRange(submissions);
        db.ModerationDecisionRecords.AddRange(decisions);
        await db.SaveChangesAsync();

        var store = new ModerationFoundationStore(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db));
        SellerVersionReviewDto? review = await store.GetOwnerVersionReview(version.Id, author.Id);
        review.Should().NotBeNull();
        review!.History.Should().HaveCount(20);
        review.History.Select(entry => entry.SubmissionId).Should().Equal(submissions.Skip(2).Reverse().Select(row => row.Id));
        review.History[0].CreatedAt.Should().Be(submissions[21].CreatedAt);
        review.ActiveSubmission.Should().Be(new SellerActiveSubmissionDto(
            submissions[21].Id, ModerationSubmissionKind.STANDARD, ModerationSubmissionState.IN_REVIEW, submissions[21].CaseRevision));
        review.History.Should().Contain(entry => entry.SubmissionId == submissions[3].Id
            && entry.DecisionOutcome == ModerationSubmissionState.CHANGES_REQUESTED
            && entry.DecisionReason == "Please clarify the source.");
        review.History.Should().Contain(entry => entry.SubmissionId == submissions[4].Id
            && entry.DecisionOutcome == ModerationSubmissionState.REJECTED
            && entry.DecisionReason == "The rejected basis reason.");
        review.History.Should().Contain(entry => entry.SubmissionId == submissions[5].Id
            && entry.DecisionOutcome == ModerationSubmissionState.APPROVED
            && entry.DecisionReason == null);
        (await store.GetOwnerVersionReview(version.Id, moderator.Id)).Should().BeNull();

        var json = System.Text.Json.JsonSerializer.Serialize(review, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        json.Should().NotContain(moderator.Id.ToString()).And.NotContain("Private approval text")
            .And.NotContain("claimHolder").And.NotContain("dispositions").And.NotContain("scores").And.NotContain("exclusion");
    }

    private static async Task<(User Author, Category Category, Asset Asset)> SeedAssetAsync(ApplicationDbContext db)
    {
        User author = TestData.CreateUser();
        Category category = TestData.CreateCategory();
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Users.Add(author);
        db.Categories.Add(category);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return (author, category, asset);
    }

    private static (AssetVersion Version1, AssetVersion Version2) CreateTwoVersions(Guid assetId) =>
        (
            TestData.CreateAssetVersion(assetId, versionNumber: 1, isCurrent: true),
            TestData.CreateAssetVersion(assetId, versionNumber: 2, isCurrent: false));

    private static async Task<(AssetVersion Version, AssetDraftWorkspace Workspace)> SeedVersionWorkspaceAsync(
        ApplicationDbContext db,
        Asset asset)
    {
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        AssetDraftWorkspace workspace = CreateVersionWorkspace(asset.Id, version.Id);
        db.AssetVersions.Add(version);
        db.AssetDraftWorkspaces.Add(workspace);
        return (version, workspace);
    }

    private static AssetDraftWorkspace CreateVersionWorkspace(Guid assetId, Guid versionId) =>
        new()
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            AssetVersionId = versionId,
            WorkspaceVersionScopeKey = versionId,
            ScopeId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            WorkspaceRevision = 1,
            CaseRevision = 1
        };

    private static CodeAnalysisReportHeader CreateReportHeader(Guid assetId, Guid versionId, string contentSha256) =>
        new()
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            AssetVersionId = versionId,
            ContentSha256 = contentSha256,
            PolicyVersion = "policy-v1",
            InputRevision = 1,
            Purpose = CodeAnalysisReportPurpose.PRODUCTION,
            ReviewEligible = true,
            IsFinalized = true,
            FinalizedAt = DateTimeOffset.UtcNow,
            ReportSchemaVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };

    private static async Task<PublicationSnapshot> SeedPublicationSnapshotAsync(
        ApplicationDbContext db,
        Asset asset,
        User author)
    {
        (AssetVersion version, AssetDraftWorkspace workspace) = await SeedVersionWorkspaceAsync(db, asset);
        ModerationSubmission submission = CreateSubmission(asset, version, workspace, author);
        CodeAnalysisReportHeader report = CreateReportHeader(asset.Id, version.Id, version.ContentSha256);
        db.ModerationSubmissions.Add(submission);
        db.CodeAnalysisReportHeaders.Add(report);
        var snapshot = new PublicationSnapshot
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            CodeAnalysisReportHeaderId = report.Id,
            ModerationSubmissionId = submission.Id,
            PolicyVersion = "policy-v1",
            ApprovedMetadataJson = "{}",
            RightsReferenceJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PublicationSnapshots.Add(snapshot);
        return snapshot;
    }

    private static ModerationSubmission CreateSubmission(
        Asset asset,
        AssetVersion version,
        AssetDraftWorkspace workspace,
        User author,
        Guid? id = null,
        Guid? workspaceVersionScopeKey = null,
        Guid? reportId = null,
        Guid? coverageAssessmentId = null,
        int sellerEvidenceRevision = 1) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = workspaceVersionScopeKey ?? workspace.WorkspaceVersionScopeKey,
            OwnerUserId = author.Id,
            ContentSha256 = version.ContentSha256,
            CodeAnalysisReportHeaderId = reportId,
            CoverageAssessmentId = coverageAssessmentId,
            DeclarationRevision = 1,
            MaterialMetadataRevision = 1,
            SellerEvidenceRevision = sellerEvidenceRevision,
            PolicyVersion = "policy-v1",
            SellerEvidenceDigest = new string('d', 64),
            State = ModerationSubmissionState.SUBMITTED,
            CaseRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
}
