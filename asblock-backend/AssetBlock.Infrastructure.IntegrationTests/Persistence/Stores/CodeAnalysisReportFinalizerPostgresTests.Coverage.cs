using System.Text;
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
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

public sealed partial class CodeAnalysisReportFinalizerPostgresTests
{
    private static ModerationFoundationStore CoverageStore(ApplicationDbContext db, IAuditWriter? writer = null) => new(db, new AssetStore(db), new UserStore(db), new EfUnitOfWork(db), auditWriter: writer ?? new AuditWriter(new AuditStore(db), Substitute.For<IAuditContextAccessor>(), NullLogger<AuditWriter>.Instance));
    private static CoverageAssessmentRequest AcceptedCoverage(CoverageReviewSaved saved) => new(Guid.NewGuid(), saved.RequestRevision, saved.TupleDigest,
        CoverageAssessmentOutcome.ACCEPTED, ["Markdown"], [new("README.md", "Markdown", "Documentation only; no core logic", true, RestrictedEvidenceKind.ARCHIVE_README)],
        true, "Review the complete stored documentation and all omitted paths.", "manual-review/documentation-complete", "Private assessor rationale.");
    private static async Task<ReviewAccessResult<CoverageReviewSaved>> AssessReadableCoverage(ModerationFoundationStore store, Guid moderatorId, CoverageReviewSaved saved, CoverageAssessmentRequest? input = null)
    {
        (await store.ReadRestrictedEvidence(moderatorId, saved.RequestId, true, RestrictedEvidenceKind.COVERAGE_INVENTORY, null)).Status.Should().Be(ReviewAccessStatus.SUCCESS);
        (await store.ReadRestrictedEvidence(moderatorId, saved.RequestId, true, RestrictedEvidenceKind.ARCHIVE_README, null)).Status.Should().Be(ReviewAccessStatus.SUCCESS);
        return await store.AssessCoverage(moderatorId, saved.RequestId, input ?? AcceptedCoverage(saved), new string('e', 64));
    }
    private static async Task<ResponseReviewSeed> SeedPartialReview(ApplicationDbContext db)
    {
        ResponseReviewSeed seed = await SeedCurrentMaterialReview(db, 1);
        await db.CodeAnalysisReportHeaders.Where(h => h.Id == seed.ReportId).ExecuteUpdateAsync(s => s.SetProperty(h => h.CoverageLevel, CodeAnalysisCoverageLevel.PARTIAL).SetProperty(h => h.OmissionCount, 1));
        const string readme = "# Documentation\nThis archive contains a small arithmetic helper.\n";
        db.AssetArchiveAnalyses.Add(new AssetArchiveAnalysis
        {
            AssetVersionId = seed.Source.Version.Id,
            FileCount = 2,
            TotalExpandedBytes = 64 + Encoding.UTF8.GetByteCount(readme),
            ReadmeContent = readme,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var artifact = JsonSerializer.Serialize(new
        {
            data = new
            {
                analyzed = new[] { new { canonicalPath = "src/main.py", size = 64 } },
                omissions = new[] { new { path = "README.md", reason = "UNSUPPORTED_EXTENSION" } },
                omissionOverflow = 0,
                coverageByDialect = new { unknown = new { omitted = 1 } }
            }
        });
        CodeAnalysisReportStage extraction = await db.CodeAnalysisReportStages.SingleAsync(s => s.ReportId == seed.ReportId && s.Stage == "EXTRACTION");
        db.CodeAnalysisReuseArtifacts.Add(new CodeAnalysisReuseArtifact
        {
            Id = Guid.NewGuid(),
            SourceReportId = seed.ReportId,
            SourceRunId = seed.Source.RunId,
            SourceStageId = extraction.Id,
            Stage = "EXTRACTION",
            ArtifactDigest = new string('a', 64),
            ArtifactJson = artifact,
            PayloadBytes = Encoding.UTF8.GetByteCount(artifact),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return seed;
    }
    private static async Task<CoverageReviewSaved> CreateCoverageRequest(ModerationFoundationStore store, ResponseReviewSeed seed)
    {
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        ReviewAccessResult<CoverageReviewSaved> result = await store.RequestCoverageReview(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id,
            new(Guid.NewGuid(), current.WorkspaceId, seed.ReportId, current.WorkspaceRevision), new string('d', 64));
        result.Status.Should().Be(ReviewAccessStatus.SUCCESS);
        return result.Value!;
    }
    private static async Task<User> CoverageModerator(ApplicationDbContext db, string role = AppRoles.MODERATOR)
    {
        User user = TestData.CreateUser("coverage-" + Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N") + "@example.test");
        user.Role = role;
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }
    [Fact]
    public async Task Coverage_WhenAcceptedForReadablePartialTuple_ShouldPermitSubmissionAndNeverOverrideSecurity()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User moderator = await CoverageModerator(db);
        CoverageReviewSaved requested = await CreateCoverageRequest(store, seed);
        (await AssessReadableCoverage(store, moderator.Id, requested)).Status.Should().Be(ReviewAccessStatus.SUCCESS);
        SellerResponsesPageDto current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        (await new SaveSellerEvidenceCommandHandler(store).Handle(ResponseCommand(seed, current, seed.Findings[0], SellerResponseType.NO_EVIDENCE, ""), CancellationToken.None)).IsSuccess.Should().BeTrue();
        current = (await store.GetOwnerResponses(seed.Source.Asset.Id, seed.Source.Version.Id, seed.Source.Asset.AuthorId, 1))!;
        Result<Guid> submitted = await new SubmitAssetVersionCommandHandler(store, ResponseAnalysisStore(db)).Handle(SubmitResponseVersion(seed, current), CancellationToken.None);
        submitted.IsSuccess.Should().BeTrue();
        (await db.ModerationSubmissions.SingleAsync()).State.Should().Be(ModerationSubmissionState.SUBMITTED);
        (await db.PublicationSnapshots.AnyAsync()).Should().BeFalse();
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("foreign")]
    [InlineData("revoked")]
    public async Task Evidence_WhenCallerCannotReview_ShouldDenyAllContent(string caller)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        CoverageReviewSaved requested = await CreateCoverageRequest(store, seed);
        User user = await CoverageModerator(db, caller == "admin" ? AppRoles.ADMIN : caller == "foreign" ? AppRoles.USER : AppRoles.MODERATOR);
        if (caller == "revoked")
        { await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER)); }
        Guid actor = caller == "owner" ? seed.Source.Asset.AuthorId : user.Id;
        ReviewAccessResult<RestrictedEvidenceDto> result = await store.ReadRestrictedEvidence(actor, requested.RequestId, true, RestrictedEvidenceKind.ARCHIVE_README, null);
        result.Status.Should().Be(ReviewAccessStatus.FORBIDDEN);
        result.Value.Should().BeNull();
        (await store.ReadRestrictedEvidence(actor, Guid.NewGuid(), true, RestrictedEvidenceKind.ARCHIVE_README, null)).Status.Should().Be(ReviewAccessStatus.FORBIDDEN);
    }
    [Fact]
    public async Task Evidence_WhenAuditFailsOrVersionIsRestricted_ShouldReleaseNothing()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        CoverageReviewSaved requested = await CreateCoverageRequest(store, seed);
        User moderator = await CoverageModerator(db);
        IAuditWriter failing = Substitute.For<IAuditWriter>();
        failing.Write(Arg.Any<AssetBlock.Domain.Core.Dto.Audit.AuditEvent>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new InvalidOperationException("private sentinel must not escape")));
        ReviewAccessResult<RestrictedEvidenceDto> denied = await CoverageStore(db, failing).ReadRestrictedEvidence(moderator.Id, requested.RequestId, true, RestrictedEvidenceKind.ARCHIVE_README, null);
        denied.Status.Should().Be(ReviewAccessStatus.AUDIT_UNAVAILABLE);
        denied.Value.Should().BeNull();
        db.ChangeTracker.Clear();
        await db.AssetVersions.Where(v => v.Id == seed.Source.Version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SafetyRestriction, AssetVersionSafetyRestriction.INFECTED).SetProperty(v => v.SafetyRestrictionReason, "MALWARE_DETECTED").SetProperty(v => v.SafetyRestrictedAt, DateTimeOffset.UtcNow));
        denied = await store.ReadRestrictedEvidence(moderator.Id, requested.RequestId, true, RestrictedEvidenceKind.ARCHIVE_README, null);
        denied.Status.Should().Be(ReviewAccessStatus.SECURITY_RESTRICTED);
        denied.Value.Should().BeNull();
    }
    [Fact]
    public async Task Evidence_WhenAllowed_ShouldCommitAuditBeforeReturningBoundedText()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        CoverageReviewSaved requested = await CreateCoverageRequest(store, seed);
        User moderator = await CoverageModerator(db);
        await db.AssetArchiveAnalyses.Where(a => a.AssetVersionId == seed.Source.Version.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.ReadmeContent, new string('x', 12000)));
        ReviewAccessResult<RestrictedEvidenceDto> result = await store.ReadRestrictedEvidence(moderator.Id, requested.RequestId, true, RestrictedEvidenceKind.ARCHIVE_README, null);
        result.Status.Should().Be(ReviewAccessStatus.SUCCESS);
        result.Value!.Truncated.Should().BeTrue();
        Encoding.UTF8.GetByteCount(result.Value.Content).Should().BeLessThanOrEqualTo(ReviewEvidenceLimits.MAX_TEXT_BYTES);
        result.Value.Content.Should().EndWith(ReviewEvidenceLimits.TRUNCATION_MARKER);
        await using ApplicationDbContext observer = fixture.CreateDbContext();
        AuditLog audit = await observer.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.RESTRICTED_EVIDENCE_READ);
        audit.ActorUserId.Should().Be(moderator.Id);
        audit.MetadataJson.Should().NotContain(new string('x', 100));
        (await store.ReadRestrictedEvidence(moderator.Id, requested.RequestId, true, (RestrictedEvidenceKind)999, null)).Status.Should().Be(ReviewAccessStatus.INVALID);
    }
    [Fact]
    public async Task Evidence_WhenCommitFailsAfterAuditWrite_ShouldReleaseNothing()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        CoverageReviewSaved request = await CreateCoverageRequest(store, seed);
        User moderator = await CoverageModerator(db);
        IAuditWriter writer = Substitute.For<IAuditWriter>();
        var inner = new AuditWriter(new AuditStore(db), Substitute.For<IAuditContextAccessor>(), NullLogger<AuditWriter>.Instance);
        writer.Write(Arg.Any<AssetBlock.Domain.Core.Dto.Audit.AuditEvent>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            await inner.Write(call.Arg<AssetBlock.Domain.Core.Dto.Audit.AuditEvent>(), call.Arg<CancellationToken>());
            db.AuditLogs.Add(new AuditLog { Action = "test", ResourceType = "test", OccurredAt = DateTimeOffset.UtcNow, ActorType = AuditActorType.SYSTEM, Outcome = (AuditOutcome)999 });
        });
        ReviewAccessResult<RestrictedEvidenceDto> result = await CoverageStore(db, writer).ReadRestrictedEvidence(moderator.Id, request.RequestId, true, RestrictedEvidenceKind.ARCHIVE_README, null);
        await writer.Received(1).Write(Arg.Any<AssetBlock.Domain.Core.Dto.Audit.AuditEvent>(), Arg.Any<CancellationToken>());
        result.Value.Should().BeNull();
        result.Status.Should().Be(ReviewAccessStatus.AUDIT_UNAVAILABLE);
    }
    [Fact]
    public async Task Evidence_WhenRoleRevocationHoldsLock_ShouldDenyAfterRevocationCommits()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        CoverageReviewSaved request = await CreateCoverageRequest(CoverageStore(db), seed);
        User moderator = await CoverageModerator(db);
        await using ApplicationDbContext revoker = fixture.CreateDbContext();
        await using Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction = await revoker.Database.BeginTransactionAsync();
        await revoker.Users.Where(u => u.Id == moderator.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        await using ApplicationDbContext reader = fixture.CreateDbContext();
        Task<ReviewAccessResult<RestrictedEvidenceDto>> pending = CoverageStore(reader).ReadRestrictedEvidence(moderator.Id, request.RequestId, true, RestrictedEvidenceKind.ARCHIVE_README, null);
        await Task.Delay(200);
        pending.IsCompleted.Should().BeFalse();
        await transaction.CommitAsync();
        ReviewAccessResult<RestrictedEvidenceDto> result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        result.Status.Should().Be(ReviewAccessStatus.FORBIDDEN);
        result.Value.Should().BeNull();
    }
    [Theory]
    [InlineData("declaration")]
    [InlineData("material")]
    [InlineData("report")]
    [InlineData("content")]
    [InlineData("policy")]
    public async Task Coverage_WhenTupleChanges_ShouldInvalidateAcceptedAssessment(string member)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User moderator = await CoverageModerator(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(store, seed);
        (await AssessReadableCoverage(store, moderator.Id, saved)).Status.Should().Be(ReviewAccessStatus.SUCCESS);
        switch (member)
        {
            case "declaration":
                await db.AssetDraftWorkspaces.Where(w => w.Id == seed.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(w => w.SourceDeclarationHeadRevision, 2));
                break;
            case "material":
                await db.AssetDraftWorkspaces.Where(w => w.Id == seed.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(w => w.MaterialMetadataHeadRevision, 2));
                break;
            case "report":
                await db.AssetVersions.Where(v => v.Id == seed.Source.Version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SelectedCodeAnalysisReportId, (Guid?)null));
                break;
            case "content":
                await db.AssetVersions.Where(v => v.Id == seed.Source.Version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.ContentSha256, new string('a', 64)));
                break;
            case "policy":
                await db.CodeAnalysisRuns.Where(r => r.Id == seed.Source.RunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.PolicyDigest, new string('b', 64)));
                break;
        }
        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync();
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync();
        (await CoverageReviewState.ReadStatus(db, version, workspace, CancellationToken.None)).Status.Should().Be("INVALIDATED");
        (await ReviewSubmissionEligibility.BlockedReason(db, version, workspace, version.SelectedCodeAnalysisReportId, CancellationToken.None)).Should().NotBeNull();
        (await db.CoverageAssessments.CountAsync()).Should().Be(1);
    }
    [Theory]
    [InlineData("restricted")]
    [InlineData("failed-stage")]
    [InlineData("missing-stage")]
    public async Task Coverage_WhenSecurityOrMandatoryStageFails_ShouldNotOverrideWithAssessment(string failure)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User moderator = await CoverageModerator(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(store, seed);
        (await AssessReadableCoverage(store, moderator.Id, saved)).Status.Should().Be(ReviewAccessStatus.SUCCESS);
        if (failure == "restricted")
        {
            await db.AssetVersions.Where(v => v.Id == seed.Source.Version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SafetyRestriction, AssetVersionSafetyRestriction.INFECTED)
                .SetProperty(v => v.SafetyRestrictionReason, "MALWARE_DETECTED").SetProperty(v => v.SafetyRestrictedAt, DateTimeOffset.UtcNow));
        }
        else if (failure == "failed-stage")
        { await db.CodeAnalysisReportStages.Where(s => s.ReportId == seed.ReportId && s.Stage == "TOKEN").ExecuteUpdateAsync(s => s.SetProperty(v => v.Outcome, CodeAnalysisStageOutcome.FAILED)); }
        else
        { await db.CodeAnalysisReportStages.Where(s => s.ReportId == seed.ReportId && s.Stage == "TOKEN").ExecuteDeleteAsync(); }
        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync();
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync();
        (await ReviewSubmissionEligibility.BlockedReason(db, version, workspace, seed.ReportId, CancellationToken.None)).Should().Be(failure == "restricted" ? ErrorCodes.ERR_VERSION_FILE_CHECKS_INCOMPLETE : ErrorCodes.ERR_ANALYSIS_REPORT_NOT_TRUSTED);
    }
    [Theory]
    [InlineData("security", ErrorCodes.ERR_VERSION_FILE_CHECKS_INCOMPLETE)]
    [InlineData("unavailable", ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE)]
    [InlineData("untrusted", ErrorCodes.ERR_ANALYSIS_REPORT_NOT_TRUSTED)]
    [InlineData("workspace", ErrorCodes.ERR_MODERATION_WORKSPACE_STALE)]
    [InlineData("declaration", ErrorCodes.ERR_DECLARATION_INCOMPLETE)]
    public async Task Coverage_WhenReportGateFails_ShouldPreserveReasonForRequestAndAssessment(string failure, string expected)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User moderator = await CoverageModerator(db);
        CoverageReviewSaved request = await CreateCoverageRequest(store, seed);
        switch (failure)
        {
            case "security":
                await db.AssetVersions.Where(v => v.Id == seed.Source.Version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SafetyRestriction, AssetVersionSafetyRestriction.INFECTED).SetProperty(v => v.SafetyRestrictionReason, "MALWARE_DETECTED").SetProperty(v => v.SafetyRestrictedAt, DateTimeOffset.UtcNow));
                break;
            case "unavailable":
                await db.AssetVersions.Where(v => v.Id == seed.Source.Version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SelectedCodeAnalysisReportId, (Guid?)null));
                break;
            case "untrusted":
                await db.CodeAnalysisReportHeaders.Where(h => h.Id == seed.ReportId).ExecuteUpdateAsync(s => s.SetProperty(h => h.ReviewEligible, false));
                break;
            case "workspace":
                await db.CodeAnalysisRuns.Where(r => r.Id == seed.Source.RunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeclarationDigest, new string('c', 64)));
                break;
            case "declaration":
                await db.AssetSourceDeclarationRevisions.Where(r => r.WorkspaceId == seed.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(r => r.PayloadJson, "{}"));
                break;
        }
        ReviewAccessResult<CoverageReviewSaved> assessed = await store.AssessCoverage(moderator.Id, request.RequestId, AcceptedCoverage(request), new string('e', 64));
        var input = new CoverageReviewCreateRequest(Guid.NewGuid(), seed.WorkspaceId, seed.ReportId, request.WorkspaceRevision);
        ReviewAccessResult<CoverageReviewSaved> requested = await store.RequestCoverageReview(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, input, new string('f', 64));
        foreach (ReviewAccessResult<CoverageReviewSaved> result in new[] { assessed, requested })
        {
            result.Status.Should().Be(failure == "security" ? ReviewAccessStatus.SECURITY_RESTRICTED : ReviewAccessStatus.CONFLICT);
            if (failure != "security")
            { result.ReasonCode.Should().Be(expected); }
        }
    }
    [Fact]
    public async Task Coverage_WhenBlockedOrAssessorRevoked_ShouldRemainBlockedWithoutLeakingRationale()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User moderator = await CoverageModerator(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(store, seed);
        CoverageAssessmentRequest blocked = AcceptedCoverage(saved) with { Outcome = CoverageAssessmentOutcome.BLOCKED, ManageableScope = false, CompletedPlanReference = "", Scope = [] };
        (await store.AssessCoverage(moderator.Id, saved.RequestId, blocked, new string('e', 64))).Status.Should().Be(ReviewAccessStatus.SUCCESS);
        SellerAnalysisSummaryDto summary = (await ResponseAnalysisStore(db).GetOwnerSummary(seed.Source.Version.Id, seed.Source.Asset.AuthorId))!;
        summary.CanSubmit.Should().BeFalse();
        summary.SubmissionBlockedReason.Should().Be(ErrorCodes.ERR_COVERAGE_ASSESSMENT_BLOCKED);
        summary.CoverageReview!.Status.Should().Be("BLOCKED");
        AuditLog assessmentAudit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.COVERAGE_REVIEW_ASSESSED);
        assessmentAudit.MetadataJson.Should().NotContain(blocked.Rationale).And.NotContain(blocked.ReviewPlan).And.NotContain("Documentation");
        var ownerJson = JsonSerializer.Serialize(summary, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var privateValue in new[] { blocked.Rationale, blocked.ReviewPlan, "competenceJson", "scopeJson", "moderatorUserId", "rationale", "tokenScore", new string('b', 64) })
        { ownerJson.Should().NotContain(privateValue); }
        await db.Users.Where(u => u.Id == moderator.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        summary = (await ResponseAnalysisStore(db).GetOwnerSummary(seed.Source.Version.Id, seed.Source.Asset.AuthorId))!;
        summary.CoverageReview!.Status.Should().Be("INVALIDATED");
    }
    [Fact]
    public async Task Coverage_WhenOperationsReplayOrConflict_ShouldPreserveReceiptBeforeStaleChecksAndRecheckAccess()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User moderator = await CoverageModerator(db);
        var input = new CoverageReviewCreateRequest(Guid.NewGuid(), seed.WorkspaceId, seed.ReportId, 1);
        SellerAnalysisSummaryDto before = (await ResponseAnalysisStore(db).GetOwnerSummary(seed.Source.Version.Id, seed.Source.Asset.AuthorId))!;
        before.CoverageReview!.Status.Should().Be("NOT_REQUESTED");
        before.CoverageReview.CanRequest.Should().BeTrue();
        ReviewAccessResult<CoverageReviewSaved> first = await store.RequestCoverageReview(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, input, new string('a', 64));
        first.Status.Should().Be(ReviewAccessStatus.SUCCESS);
        SellerAnalysisSummaryDto pending = (await ResponseAnalysisStore(db).GetOwnerSummary(seed.Source.Version.Id, seed.Source.Asset.AuthorId))!;
        pending.CoverageReview!.Status.Should().Be("REQUESTED");
        pending.CoverageReview.CanRequest.Should().BeFalse();
        (await store.RequestCoverageReview(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, input, new string('a', 64))).Value!.Replayed.Should().BeTrue();
        (await store.RequestCoverageReview(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, input, new string('b', 64))).Status.Should().Be(ReviewAccessStatus.CONFLICT);
        (await store.RequestCoverageReview(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, input with { OperationId = Guid.NewGuid() }, new string('c', 64))).Status.Should().Be(ReviewAccessStatus.STALE);
        (await store.RequestCoverageReview(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, input with { OperationId = Guid.NewGuid(), ExpectedWorkspaceRevision = 2 }, new string('d', 64))).Status.Should().Be(ReviewAccessStatus.CONFLICT);
        CoverageAssessmentRequest assess = AcceptedCoverage(first.Value!);
        (await store.AssessCoverage(seed.Source.Asset.AuthorId, first.Value!.RequestId, assess, new string('e', 64))).Status.Should().Be(ReviewAccessStatus.FORBIDDEN);
        (await AssessReadableCoverage(store, moderator.Id, first.Value, assess)).Status.Should().Be(ReviewAccessStatus.SUCCESS);
        (await store.AssessCoverage(moderator.Id, first.Value.RequestId, assess, new string('e', 64))).Value!.Replayed.Should().BeTrue();
        (await store.AssessCoverage(moderator.Id, first.Value.RequestId, assess, new string('f', 64))).Status.Should().Be(ReviewAccessStatus.CONFLICT);
        await db.Users.Where(u => u.Id == moderator.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
        (await store.AssessCoverage(moderator.Id, first.Value.RequestId, assess, new string('e', 64))).Status.Should().Be(ReviewAccessStatus.FORBIDDEN);
        (await db.CoverageReviewRequests.CountAsync()).Should().Be(1);
        (await db.CoverageAssessments.CountAsync()).Should().Be(1);
        (await db.AuditLogs.CountAsync(a => a.Action == AuditActions.COVERAGE_REVIEW_REQUESTED)).Should().Be(1);
        (await db.AuditLogs.CountAsync(a => a.Action == AuditActions.COVERAGE_REVIEW_ASSESSED)).Should().Be(1);
    }

    [Theory]
    [InlineData(CoverageAssessmentOutcome.ACCEPTED, false)]
    [InlineData(CoverageAssessmentOutcome.BLOCKED, false)]
    [InlineData(CoverageAssessmentOutcome.ACCEPTED, true)]
    [InlineData(CoverageAssessmentOutcome.BLOCKED, true)]
    public async Task Coverage_WhenAssessmentExists_ShouldMatchRequestHint(CoverageAssessmentOutcome outcome, bool revoke)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User moderator = await CoverageModerator(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(store, seed);
        CoverageAssessmentRequest assessment = AcceptedCoverage(saved) with { Outcome = outcome };
        ReviewAccessResult<CoverageReviewSaved> assessed = outcome == CoverageAssessmentOutcome.ACCEPTED
            ? await AssessReadableCoverage(store, moderator.Id, saved, assessment)
            : await store.AssessCoverage(moderator.Id, saved.RequestId, assessment, new string('e', 64));
        assessed.Status.Should().Be(ReviewAccessStatus.SUCCESS);
        if (revoke)
        { await db.Users.Where(u => u.Id == moderator.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER)); }
        SellerAnalysisSummaryDto summary = (await ResponseAnalysisStore(db).GetOwnerSummary(seed.Source.Version.Id, seed.Source.Asset.AuthorId))!;
        summary.CoverageReview!.CanRequest.Should().Be(revoke);
        if (revoke)
        { summary.CoverageReview.ReasonCode.Should().Be(ErrorCodes.ERR_COVERAGE_ASSESSOR_UNAVAILABLE); }
        var input = new CoverageReviewCreateRequest(Guid.NewGuid(), seed.WorkspaceId, seed.ReportId, saved.WorkspaceRevision);
        ReviewAccessResult<CoverageReviewSaved> result = await store.RequestCoverageReview(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, input, new string('f', 64));
        (result.Status == ReviewAccessStatus.SUCCESS).Should().Be(summary.CoverageReview.CanRequest);
        if (!revoke)
        { result.Status.Should().Be(ReviewAccessStatus.CONFLICT); }
    }
    [Fact]
    public async Task Coverage_WhenBlockedTupleChanges_ShouldAllowNewRequest()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User moderator = await CoverageModerator(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(store, seed);
        CoverageAssessmentRequest blocked = AcceptedCoverage(saved) with { Outcome = CoverageAssessmentOutcome.BLOCKED };
        (await store.AssessCoverage(moderator.Id, saved.RequestId, blocked, new string('e', 64))).Status.Should().Be(ReviewAccessStatus.SUCCESS);
        var changed = new string('c', 64);
        await db.AssetSourceDeclarationRevisions.Where(r => r.WorkspaceId == seed.WorkspaceId).ExecuteUpdateAsync(s => s.SetProperty(r => r.ContentDigest, changed));
        await db.CodeAnalysisRuns.Where(r => r.Id == seed.Source.RunId).ExecuteUpdateAsync(s => s.SetProperty(r => r.DeclarationDigest, changed));
        SellerAnalysisSummaryDto summary = (await ResponseAnalysisStore(db).GetOwnerSummary(seed.Source.Version.Id, seed.Source.Asset.AuthorId))!;
        summary.CoverageReview!.Status.Should().Be("INVALIDATED");
        summary.CoverageReview.CanRequest.Should().BeTrue();
        var input = new CoverageReviewCreateRequest(Guid.NewGuid(), seed.WorkspaceId, seed.ReportId, saved.WorkspaceRevision);
        (await store.RequestCoverageReview(seed.Source.Asset.AuthorId, seed.Source.Asset.Id, seed.Source.Version.Id, input, new string('f', 64))).Status.Should().Be(ReviewAccessStatus.SUCCESS);
    }
    [Theory]
    [InlineData("coverageByDialect")]
    [InlineData("omissionOverflow")]
    public async Task Evidence_WhenArtifactPropertyMissing_ShouldReleaseNothing(string property)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        CoverageReviewSaved request = await CreateCoverageRequest(store, seed);
        User moderator = await CoverageModerator(db);
        System.Text.Json.Nodes.JsonNode artifact = System.Text.Json.Nodes.JsonNode.Parse(await db.CodeAnalysisReuseArtifacts.Select(a => a.ArtifactJson).SingleAsync())!;
        artifact["data"]!.AsObject().Remove(property);
        var json = artifact.ToJsonString();
        await db.CodeAnalysisReuseArtifacts.ExecuteUpdateAsync(s => s.SetProperty(a => a.ArtifactJson, json));
        ReviewAccessResult<RestrictedEvidenceDto> result = await store.ReadRestrictedEvidence(moderator.Id, request.RequestId, true, RestrictedEvidenceKind.COVERAGE_INVENTORY, null);
        result.Status.Should().Be(ReviewAccessStatus.NOT_FOUND);
        result.Value.Should().BeNull();
    }

    [Fact]
    public async Task Coverage_WhenAssessorHasNotReadAuditedScope_ShouldRejectAcceptance()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User moderator = await CoverageModerator(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(store, seed);
        (await store.AssessCoverage(moderator.Id, saved.RequestId, AcceptedCoverage(saved), new string('e', 64))).Status.Should().Be(ReviewAccessStatus.SCOPE_UNAVAILABLE);
        (await db.CoverageAssessments.AnyAsync()).Should().BeFalse();
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("competence")]
    [InlineData("scope")]
    [InlineData("overflow")]
    [InlineData("incomplete")]
    [InlineData("unknown-path")]
    public async Task Coverage_WhenManualScopeCannotBeCompleted_ShouldRejectAcceptance(string reason)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        User moderator = await CoverageModerator(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(store, seed);
        CoverageAssessmentRequest input = AcceptedCoverage(saved);
        input = reason switch
        {
            "unreadable" => input with { Scope = [input.Scope[0] with { Readable = false }] },
            "competence" => input with { Competence = ["Python"] },
            "scope" => input with { ManageableScope = false },
            "unknown-path" => input with { Scope = [input.Scope[0] with { Path = "other.md" }] },
            _ => input
        };
        if (reason == "incomplete")
        { await db.AssetArchiveAnalyses.ExecuteUpdateAsync(s => s.SetProperty(a => a.TotalExpandedBytes, 10000)); }
        if (reason == "overflow")
        { System.Text.Json.Nodes.JsonNode artifact = System.Text.Json.Nodes.JsonNode.Parse(await db.CodeAnalysisReuseArtifacts.Select(a => a.ArtifactJson).SingleAsync())!; artifact["data"]!["omissionOverflow"] = 1; var json = artifact.ToJsonString(); await db.CodeAnalysisReuseArtifacts.ExecuteUpdateAsync(s => s.SetProperty(a => a.ArtifactJson, json)); }
        (await AssessReadableCoverage(store, moderator.Id, saved, input)).Status.Should().Be(ReviewAccessStatus.SCOPE_UNAVAILABLE);
        (await db.CoverageAssessments.AnyAsync()).Should().BeFalse();
    }
    [Fact]
    public async Task Coverage_WhenOwnerIsModeratorOrAssessorIsAdmin_ShouldDenyAssessment()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(store, seed);
        await db.Users.Where(u => u.Id == seed.Source.Asset.AuthorId).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.MODERATOR));
        User admin = await CoverageModerator(db, AppRoles.ADMIN);
        foreach (Guid actor in new[] { seed.Source.Asset.AuthorId, admin.Id })
        {
            (await store.AssessCoverage(actor, saved.RequestId, AcceptedCoverage(saved), new string('e', 64))).Status.Should().Be(ReviewAccessStatus.FORBIDDEN);
        }
        (await store.GetCoverageQueue(seed.Source.Asset.AuthorId, 1)).Value!.Items.Should().BeEmpty();
        (await store.GetCoverageQueue(admin.Id, 1)).Status.Should().Be(ReviewAccessStatus.FORBIDDEN);
        User moderator = await CoverageModerator(db);
        (await store.GetCoverageQueue(moderator.Id, 1)).Value!.Items.Select(i => i.RequestId).Should().Equal(saved.RequestId);
        (await store.GetCoverageQueue(moderator.Id, 2)).Value!.Items.Should().BeEmpty();
        (await store.GetCoverageQueue(moderator.Id, 0)).Status.Should().Be(ReviewAccessStatus.INVALID);
    }
    [Fact]
    public async Task Coverage_WhenTwoAssessorsRace_ShouldAppendExactlyOneAssessment()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(CoverageStore(db), seed);
        User first = await CoverageModerator(db);
        User second = await CoverageModerator(db);
        await using ApplicationDbContext left = fixture.CreateDbContext();
        await using ApplicationDbContext right = fixture.CreateDbContext();
        CoverageAssessmentRequest input = AcceptedCoverage(saved) with { Outcome = CoverageAssessmentOutcome.BLOCKED, Scope = [] };
        ReviewAccessResult<CoverageReviewSaved>[] results = await Task.WhenAll(
            CoverageStore(left).AssessCoverage(first.Id, saved.RequestId, input, new string('e', 64)),
            CoverageStore(right).AssessCoverage(second.Id, saved.RequestId, input with { OperationId = Guid.NewGuid() }, new string('f', 64)));
        results.Count(r => r.Status == ReviewAccessStatus.SUCCESS).Should().Be(1);
        results.Count(r => r.Status == ReviewAccessStatus.STALE).Should().Be(1);
        (await db.CoverageAssessments.CountAsync()).Should().Be(1);
    }
    [Theory]
    [InlineData("duplicate-open")]
    [InlineData("unknown-outcome")]
    [InlineData("empty-plan")]
    [InlineData("wrong-workspace")]
    public async Task Coverage_WhenDatabaseConstraintIsViolated_ShouldRejectWrite(string violation)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(store, seed);
        User moderator = await CoverageModerator(db);
        Func<Task> write;
        if (violation is "duplicate-open" or "wrong-workspace")
        {
            Guid workspace = violation == "wrong-workspace" ? Guid.NewGuid() : seed.WorkspaceId;
            var tupleDigest = violation == "wrong-workspace" ? new string('f', 64) : saved.TupleDigest;
            write = () => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO coverage_review_requests SELECT {Guid.NewGuid()}, "AssetId", "AssetVersionId", {workspace}, "ReportId", "OwnerUserId", "ContentSha256", "DeclarationRevision", "MaterialRevision", "SellerEvidenceRevision", "PolicyVersion", "PolicyDigest", {tupleDigest}, "RequestedWorkspaceRevision", "Revision", "IsOpen", "CreatedAt", "UpdatedAt" FROM coverage_review_requests WHERE "Id" = {saved.RequestId}
                """);
        }
        else
        {
            var outcome = violation == "unknown-outcome" ? "UNKNOWN" : "ACCEPTED";
            write = () => db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO coverage_assessments ("Id", "RequestId", "ModeratorUserId", "Outcome", "CompetenceJson", "ScopeJson", "ManageableScope", "ReviewPlan", "CompletedPlanReference", "Rationale", "CreatedAt") VALUES ({Guid.NewGuid()}, {saved.RequestId}, {moderator.Id}, {outcome}, '[]'::jsonb, '[]'::jsonb, FALSE, 'Review', '', 'Reason', CURRENT_TIMESTAMP)
                """);
        }
        AwesomeAssertions.Specialized.ExceptionAssertions<Npgsql.PostgresException> error = await write.Should().ThrowAsync<Npgsql.PostgresException>();
        error.Which.SqlState.Should().Be(violation == "wrong-workspace" ? "23503" : violation == "duplicate-open" ? "23505" : "23514");
    }
    [Fact]
    public async Task Coverage_WhenAssessmentAlreadyExists_ShouldRejectDuplicateAndMutation()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        ResponseReviewSeed seed = await SeedPartialReview(db);
        ModerationFoundationStore store = CoverageStore(db);
        CoverageReviewSaved saved = await CreateCoverageRequest(store, seed);
        User moderator = await CoverageModerator(db);
        (await AssessReadableCoverage(store, moderator.Id, saved)).Status.Should().Be(ReviewAccessStatus.SUCCESS);
        Func<Task> duplicate = () => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO coverage_assessments SELECT {Guid.NewGuid()}, "RequestId", "ModeratorUserId", "Outcome", "CompetenceJson", "ScopeJson", "ManageableScope", "ReviewPlan", "CompletedPlanReference", "Rationale", "CreatedAt", "UpdatedAt" FROM coverage_assessments WHERE "RequestId" = {saved.RequestId}
            """);
        await duplicate.Should().ThrowAsync<Npgsql.PostgresException>();
        CoverageAssessment assessment = await db.CoverageAssessments.SingleAsync();
        db.CoverageAssessments.Remove(assessment);
        Func<Task> remove = () => db.SaveChangesAsync();
        await remove.Should().ThrowAsync<InvalidOperationException>();
    }

}
