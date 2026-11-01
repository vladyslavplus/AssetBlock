using System.Net;
using System.Net.Http.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class ModerationDecisionIntegrationTests(IntegrationTestFixture fixture)
{
    // Admission producer identity is owned by CodeAnalysisReportFinalizer.
    private const string FINALIZER_ID = "assetblock-dotnet-finalizer";
    private static readonly string CONTENT_SHA256 = new('a', 64);
    private const string PRIVATE_DECISION_TEXT = "decision private sentinel";

    private sealed record CaseSeed(Guid SubmissionId, Guid VersionId, Guid ReportId, Guid FindingId);

    private async Task<CaseSeed> SeedCase(string ownerUsername, ModerationSubmissionState state = ModerationSubmissionState.SUBMITTED,
        string? claimHolderUsername = null, bool withDecision = false)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User owner = await db.Users.SingleAsync(u => u.Username == ownerUsername);
        Guid? holderId = claimHolderUsername is null ? null : (await db.Users.SingleAsync(u => u.Username == claimHolderUsername)).Id;
        var category = new Category { Id = Guid.NewGuid(), Name = "Decision", Slug = "decision-" + Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
        var asset = new Asset { Id = Guid.NewGuid(), AuthorId = owner.Id, CategoryId = category.Id, Title = "Decision fixture", Description = "", Price = 1m, CreatedAt = DateTimeOffset.UtcNow };
        AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        var version = new AssetVersion
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "decision-fixture/" + Guid.NewGuid().ToString("N") + "/source.bin",
            FileName = "source.zip",
            ContentLength = 1,
            ContentSha256 = CONTENT_SHA256,
            ReleaseNotes = "Fixture",
            LicenseCode = license.Code,
            LicenseTemplateVersion = license.TemplateVersion,
            LicenseDisplayName = license.DisplayName,
            LicenseTerms = license.TermsPlainText,
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var workspace = new AssetDraftWorkspace { Id = Guid.NewGuid(), AssetId = asset.Id, AssetVersionId = version.Id, WorkspaceVersionScopeKey = version.Id, ScopeId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
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
        var finding = new CodeAnalysisReportFinding
        {
            Id = Guid.NewGuid(),
            ReportId = report.Id,
            FindingKey = "decision-fixture",
            FindingType = "POTENTIAL_MATCH",
            OwnScopePath = "own.py",
            OwnExcerpt = "decision own excerpt",
            ForeignSensitivity = CodeAnalysisFindingSensitivity.PRIVATE,
            ReasonText = "Fixture evidence",
            RequestedAction = "Review",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var submission = new ModerationSubmission
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            OwnerUserId = owner.Id,
            ContentSha256 = version.ContentSha256,
            CodeAnalysisReportHeaderId = report.Id,
            DeclarationRevision = 1,
            MaterialMetadataRevision = 1,
            SellerEvidenceRevision = 0,
            SellerEvidenceDigest = new string('b', 64),
            PolicyVersion = ReviewAnalysisPolicy.KEY,
            State = state,
            CaseRevision = 1,
            ClaimHolderId = state == ModerationSubmissionState.IN_REVIEW ? holderId : null,
            ClaimedAt = state == ModerationSubmissionState.IN_REVIEW ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Categories.Add(category);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        db.AssetDraftWorkspaces.Add(workspace);
        db.CodeAnalysisReportHeaders.Add(report);
        db.CodeAnalysisReportFindings.Add(finding);
        db.ModerationSubmissions.Add(submission);
        if (withDecision)
        {
            db.ModerationDecisionRecords.Add(new ModerationDecisionRecord
            {
                Id = Guid.NewGuid(),
                SubmissionId = submission.Id,
                AssetId = asset.Id,
                AssetVersionId = version.Id,
                ModeratorUserId = holderId!.Value,
                Outcome = ModerationSubmissionState.CHANGES_REQUESTED,
                CaseRevision = 1,
                Message = PRIVATE_DECISION_TEXT
            });
        }
        await db.SaveChangesAsync();
        return new(submission.Id, version.Id, report.Id, finding.Id);
    }

    private static string DecisionPath(CaseSeed seed) => "/" + ApiRoutes.Moderation.DECISION.Replace("{id:guid}", seed.SubmissionId.ToString(), StringComparison.Ordinal);

    private static async Task PrepareAdmittedAppealBasis(ApplicationDbContext db, ModerationSubmission original)
    {
        AssetVersion version = await db.AssetVersions.SingleAsync(v => v.Id == original.AssetVersionId);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.SingleAsync(w => w.Id == original.WorkspaceId);
        workspace.SourceDeclarationHeadRevision = 1;
        workspace.MaterialMetadataHeadRevision = 1;
        var declarationId = Guid.NewGuid();
        var materialId = Guid.NewGuid();
        const string declaration = """{"redistributionAcknowledged":true,"ownContributionSummary":"Original implementation.","components":[]}""";
        var material = System.Text.Json.JsonSerializer.Serialize(new { title = "Decision fixture", description = (string?)null, categoryId = (await db.Assets.SingleAsync(a => a.Id == original.AssetId)).CategoryId, tags = Array.Empty<string>() });
        var declarationDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(declaration)));
        var materialDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
        db.AssetSourceDeclarationRevisions.Add(new AssetSourceDeclarationRevision
        {
            Id = declarationId,
            AssetId = original.AssetId,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            Revision = 1,
            AuthorUserId = original.OwnerUserId,
            ContentDigest = declarationDigest,
            SchemaVersion = 1,
            PayloadJson = declaration,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.AssetMaterialMetadataRevisions.Add(new AssetMaterialMetadataRevision
        {
            Id = materialId,
            AssetId = original.AssetId,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            Revision = 1,
            AuthorUserId = original.OwnerUserId,
            ContentDigest = materialDigest,
            SchemaVersion = 1,
            PayloadJson = material,
            CreatedAt = DateTimeOffset.UtcNow
        });
        AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.Chain chain = AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.BuildChain() with { PolicyDigest = ReviewAnalysisPolicy.DIGEST };
        CodeAnalysisPolicyVersion? existingPolicy = await db.CodeAnalysisPolicyVersions.AsNoTracking().FirstOrDefaultAsync(p => p.PolicyKey == ReviewAnalysisPolicy.KEY && p.PolicyDigest == ReviewAnalysisPolicy.DIGEST);
        if (existingPolicy is null)
        { db.CodeAnalysisPolicyVersions.Add(AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.Policy(chain)); }
        else
        { chain = chain with { PolicyVersionId = existingPolicy.Id }; }
        AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.SeedObservedProof(db, version, chain);
        await db.SaveChangesAsync();
        CodeAnalysisRun run = AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.CompletedRun(version, chain);
        run.AssetDraftWorkspaceId = workspace.Id;
        run.DeclarationRevisionId = declarationId;
        run.MaterialRevisionId = materialId;
        run.DeclarationDigest = declarationDigest;
        run.MaterialDigest = materialDigest;
        db.CodeAnalysisRuns.Add(run);
        await db.SaveChangesAsync();
        CodeAnalysisReportHeader report = AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.Header(version, chain);
        report.Disposition = CodeAnalysisReportDisposition.SELECTED;
        report.AnalyzedFileCount = 1;
        report.OmissionCount = 0;
        report.FindingCount = 0;
        db.CodeAnalysisReportHeaders.Add(report);
        await db.SaveChangesAsync();
        CodeAnalysisReportAdmission admission = AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.Admission(report, chain, true);
        admission.AdmittedBy = FINALIZER_ID;
        db.CodeAnalysisReportAdmissions.Add(admission);
        foreach (var stage in AssetBlock.Infrastructure.Persistence.Stores.CodeAnalysisReportFinalizer.StageNames)
        {
            db.CodeAnalysisReportStages.Add(new CodeAnalysisReportStage
            {
                Id = Guid.NewGuid(),
                ReportId = report.Id,
                Stage = stage,
                Applicability = CodeAnalysisStageApplicability.MANDATORY,
                Outcome = stage == "VERIFIER" ? CodeAnalysisStageOutcome.NOT_APPLICABLE : CodeAnalysisStageOutcome.SUCCEEDED,
                ErrorCode = stage == "VERIFIER" ? "POLICY_DISABLED" : null,
                Retryable = false,
                DurationMilliseconds = 0,
                ToolIdentityJson = "{}",
                CountsJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        await db.SaveChangesAsync();
        await db.AssetVersions.Where(v => v.Id == version.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.SelectedCodeAnalysisReportId, report.Id));
        await db.ModerationSubmissions.Where(s => s.Id == original.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CodeAnalysisReportHeaderId, report.Id));
    }

    [Fact]
    public async Task Appeal_WhenEligibleOwnerRequests_ShouldCreateReplayAndShowPendingReview()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient moderator, var moderatorName, _) = await RegisterModerator();
        using (owner)
        using (moderator)
        {
            CaseSeed seed = await SeedCase(ownerName, ModerationSubmissionState.REJECTED, moderatorName, true);
            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ModerationSubmission original = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == seed.SubmissionId);
            await db.ModerationDecisionRecords.Where(d => d.SubmissionId == original.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Outcome, ModerationSubmissionState.REJECTED));
            await PrepareAdmittedAppealBasis(db, original);
            var request = new { workspaceId = original.WorkspaceId, operationId = Guid.NewGuid(), expectedWorkspaceRevision = 1, expectedCaseRevision = 1, rationale = "Independent review please." };
            AuthenticatedEncryptedUploadAcceptanceTests.ReviewEndpoint endpoint = AuthenticatedEncryptedUploadAcceptanceTests.ReadReviewEndpoint("appeal", original.AssetId, seed.VersionId);
            using HttpResponseMessage created = await owner.PostAsJsonAsync(endpoint.Path, request);
            created.StatusCode.Should().Be(HttpStatusCode.OK, await created.Content.ReadAsStringAsync());
            using var json = System.Text.Json.JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            Guid appealId = json.RootElement.GetProperty("submissionId").GetGuid();
            using HttpResponseMessage replay = await owner.PostAsJsonAsync(endpoint.Path, request);
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            (await replay.Content.ReadAsStringAsync()).Should().Contain(appealId.ToString());
            using HttpResponseMessage changed = await owner.PostAsJsonAsync(endpoint.Path, request with { rationale = "Different request" });
            changed.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await changed.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT);
            using HttpResponseMessage duplicate = await owner.PostAsJsonAsync(endpoint.Path, request with { operationId = Guid.NewGuid() });
            duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await duplicate.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_APPEAL_EXISTS);
            var reviewPath = "/" + ApiRoutes.Users.BASE + "/" + ApiRoutes.Users.ME_ASSET_VERSION_REVIEW.Replace("{assetVersionId:guid}", seed.VersionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage review = await owner.GetAsync(reviewPath);
            review.StatusCode.Should().Be(HttpStatusCode.OK);
            using var projection = System.Text.Json.JsonDocument.Parse(await review.Content.ReadAsStringAsync());
            projection.RootElement.GetProperty("canAppeal").GetBoolean().Should().BeFalse();
            projection.RootElement.GetProperty("appeal").GetProperty("state").GetString().Should().Be("PENDING_REVIEW");
            using HttpResponseMessage queue = await moderator.GetAsync("/" + ApiRoutes.Moderation.QUEUE);
            queue.StatusCode.Should().Be(HttpStatusCode.OK);
            (await queue.Content.ReadAsStringAsync()).Should().NotContain(appealId.ToString());
            var claimPath = "/" + ApiRoutes.Moderation.CLAIM.Replace("{id:guid}", appealId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage claim = await moderator.PostAsJsonAsync(claimPath, new { operationId = Guid.NewGuid(), expectedCaseRevision = 2 });
            claim.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await claim.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED);
            (await db.ModerationSubmissions.AsNoTracking().CountAsync(s => s.AssetVersionId == seed.VersionId)).Should().Be(2);
        }
    }

    [Fact]
    public async Task Decision_WhenSellerResponseIsOutstanding_ShouldReturnDecisionSpecificConflict()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient moderator, var moderatorName, _) = await RegisterModerator();
        using (owner)
        using (moderator)
        {
            CaseSeed seed = await SeedCase(ownerName, ModerationSubmissionState.IN_REVIEW, moderatorName);
            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ModerationSubmission original = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == seed.SubmissionId);
            await PrepareAdmittedAppealBasis(db, original);
            Guid reportId = (await db.AssetVersions.AsNoTracking().SingleAsync(v => v.Id == seed.VersionId)).SelectedCodeAnalysisReportId!.Value;
            var findingId = Guid.NewGuid();
            db.CodeAnalysisReportFindings.Add(new CodeAnalysisReportFinding
            {
                Id = findingId,
                ReportId = reportId,
                FindingKey = "required-response",
                FindingType = "POTENTIAL_MATCH",
                OwnScopePath = "own.py",
                OwnExcerpt = "private",
                ForeignSensitivity = CodeAnalysisFindingSensitivity.PRIVATE,
                ReasonText = "Response required",
                RequestedAction = "Respond",
                RetrievalEvidenceJson = "{\"material\":true}",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await db.CodeAnalysisReportHeaders.Where(r => r.Id == reportId).ExecuteUpdateAsync(s => s.SetProperty(r => r.FindingCount, 1));
            var emptyDigest = AssetBlock.Infrastructure.Persistence.ProductDigests.EmptyProjectionDigest(nameof(AssetSellerEvidenceRevision));
            await db.ModerationSubmissions.Where(s => s.Id == original.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.SellerEvidenceDigest, emptyDigest));
            var body = new
            {
                operationId = Guid.NewGuid(),
                basis = new
                {
                    versionId = seed.VersionId,
                    contentSha256 = CONTENT_SHA256,
                    analysisReportId = reportId,
                    declarationRevision = 1,
                    materialMetadataRevision = 1,
                    policyVersion = ReviewAnalysisPolicy.KEY,
                    sellerEvidenceRevision = 0,
                    caseRevision = 1
                },
                outcome = "REJECT",
                rationale = "Private decision marker",
                dispositions = new[] { new { findingId, disposition = "EXPLAINED", rationale = "Explanation" } },
                manualPlanCompletionReference = (string?)null
            };
            using HttpResponseMessage response = await moderator.PostAsJsonAsync(DecisionPath(seed), body);
            response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
            (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_DECISION_RESPONSES_OUTSTANDING).And.NotContain("Private decision marker");
            (await db.ModerationDecisionRecords.CountAsync(d => d.SubmissionId == original.Id)).Should().Be(0);
        }
    }

    [Theory]
    [InlineData("operation")]
    [InlineData("emptyOperation")]
    [InlineData("invalidOperation")]
    [InlineData("workspace")]
    [InlineData("invalidWorkspace")]
    [InlineData("workspaceRevision")]
    [InlineData("zeroWorkspaceRevision")]
    [InlineData("caseRevision")]
    [InlineData("zeroCaseRevision")]
    [InlineData("empty")]
    [InlineData("missingRationale")]
    [InlineData("nullRationale")]
    [InlineData("long")]
    [InlineData("unknown")]
    public async Task Appeal_WhenBodyIsInvalid_ShouldReturnBadRequestWithoutWriting(string invalid)
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        {
            CaseSeed seed = await SeedCase(ownerName);
            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ModerationSubmission original = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == seed.SubmissionId);
            var body = new Dictionary<string, object?>
            {
                ["workspaceId"] = original.WorkspaceId,
                ["operationId"] = Guid.NewGuid(),
                ["expectedWorkspaceRevision"] = 1,
                ["expectedCaseRevision"] = 1,
                ["rationale"] = "Private rationale marker"
            };
            if (invalid == "operation")
            { body.Remove("operationId"); }
            if (invalid == "emptyOperation")
            { body["operationId"] = Guid.Empty; }
            if (invalid == "invalidOperation")
            { body["operationId"] = "not-a-guid"; }
            if (invalid == "workspace")
            { body.Remove("workspaceId"); }
            if (invalid == "invalidWorkspace")
            { body["workspaceId"] = "not-a-guid"; }
            if (invalid == "workspaceRevision")
            { body.Remove("expectedWorkspaceRevision"); }
            if (invalid == "zeroWorkspaceRevision")
            { body["expectedWorkspaceRevision"] = 0; }
            if (invalid == "caseRevision")
            { body.Remove("expectedCaseRevision"); }
            if (invalid == "zeroCaseRevision")
            { body["expectedCaseRevision"] = 0; }
            if (invalid == "empty")
            { body["rationale"] = "   "; }
            if (invalid == "missingRationale")
            { body.Remove("rationale"); }
            if (invalid == "nullRationale")
            { body["rationale"] = null; }
            if (invalid == "long")
            { body["rationale"] = new string(' ', 4001) + "x"; }
            if (invalid == "unknown")
            { body["unexpected"] = true; }
            var path = "/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_APPEALS.Replace("{id:guid}", original.AssetId.ToString(), StringComparison.Ordinal).Replace("{versionId:guid}", seed.VersionId.ToString(), StringComparison.Ordinal);
            AuthenticatedEncryptedUploadAcceptanceTests.ReviewEndpoint shared = AuthenticatedEncryptedUploadAcceptanceTests.ReadReviewEndpoint("appeal", original.AssetId, seed.VersionId);
            shared.Path.Should().Be(path);
            shared.WriteMethod.Should().Be("POST");
            using HttpResponseMessage response = await owner.PostAsJsonAsync(path, body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("Private rationale marker");
            (await db.ModerationSubmissions.CountAsync(s => s.AssetVersionId == seed.VersionId)).Should().Be(1);
        }
    }

    [Fact]
    public async Task Appeal_WhenCallerIsForeignOrCaseIsNotRejected_ShouldDeny()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient foreign, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient moderator, var moderatorName, _) = await RegisterModerator();
        using (owner)
        using (foreign)
        using (moderator)
        using (HttpClient anonymous = fixture.Factory.CreateClient())
        {
            CaseSeed seed = await SeedCase(ownerName, ModerationSubmissionState.CHANGES_REQUESTED, moderatorName, true);
            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ModerationSubmission original = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == seed.SubmissionId);
            var body = new { workspaceId = original.WorkspaceId, operationId = Guid.NewGuid(), expectedWorkspaceRevision = 1, expectedCaseRevision = 1, rationale = "Private rationale marker" };
            var path = "/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_APPEALS.Replace("{id:guid}", original.AssetId.ToString(), StringComparison.Ordinal).Replace("{versionId:guid}", seed.VersionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage noAuth = await anonymous.PostAsJsonAsync(path, body);
            noAuth.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            using HttpResponseMessage denied = await foreign.PostAsJsonAsync(path, body);
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            using HttpResponseMessage invalidState = await owner.PostAsJsonAsync(path, body);
            invalidState.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await invalidState.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_APPEAL_NOT_ALLOWED).And.NotContain("Private rationale marker");
            (await db.ModerationSubmissions.CountAsync(s => s.AssetVersionId == seed.VersionId)).Should().Be(1);
        }
    }

    [Fact]
    public async Task Appeal_WhenBasisChangesOrAlreadyExists_ShouldReturnExactConflictAndHideFromDecider()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient moderator, var moderatorName, _) = await RegisterModerator();
        using (owner)
        using (moderator)
        {
            CaseSeed seed = await SeedCase(ownerName, ModerationSubmissionState.REJECTED, moderatorName, true);
            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ModerationSubmission original = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == seed.SubmissionId);
            await db.ModerationDecisionRecords.Where(d => d.SubmissionId == original.Id).ExecuteUpdateAsync(s => s.SetProperty(d => d.Outcome, ModerationSubmissionState.REJECTED));
            Guid decisionId = await db.ModerationDecisionRecords.Where(d => d.SubmissionId == original.Id).Select(d => d.Id).SingleAsync();
            var body = new { workspaceId = original.WorkspaceId, operationId = Guid.NewGuid(), expectedWorkspaceRevision = 1, expectedCaseRevision = 1, rationale = "Private rationale marker" };
            var path = "/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_APPEALS.Replace("{id:guid}", original.AssetId.ToString(), StringComparison.Ordinal).Replace("{versionId:guid}", seed.VersionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage changed = await owner.PostAsJsonAsync(path, body);
            changed.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await changed.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_APPEAL_BASIS_CHANGED).And.NotContain("Private rationale marker");
            var appeal = new ModerationSubmission
            {
                Id = Guid.NewGuid(),
                Kind = ModerationSubmissionKind.APPEAL,
                AppealOfDecisionId = decisionId,
                AppealRationale = "private",
                AssetId = original.AssetId,
                AssetVersionId = original.AssetVersionId,
                WorkspaceId = original.WorkspaceId,
                WorkspaceVersionScopeKey = original.WorkspaceVersionScopeKey,
                OwnerUserId = original.OwnerUserId,
                ContentSha256 = original.ContentSha256,
                CodeAnalysisReportHeaderId = original.CodeAnalysisReportHeaderId,
                DeclarationRevision = original.DeclarationRevision,
                MaterialMetadataRevision = original.MaterialMetadataRevision,
                SellerEvidenceRevision = original.SellerEvidenceRevision,
                SellerEvidenceDigest = original.SellerEvidenceDigest,
                PolicyVersion = original.PolicyVersion,
                State = ModerationSubmissionState.SUBMITTED,
                CaseRevision = 2,
                PreviousSubmissionId = original.Id,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(1)
            };
            db.ModerationSubmissions.Add(appeal);
            await db.SaveChangesAsync();
            var requestDigest = AssetBlock.Application.Common.DraftPayloadJson.ComputeDigest(AssetBlock.Application.Common.DraftPayloadJson.Serialize(new
            {
                OwnerId = original.OwnerUserId,
                AssetId = original.AssetId,
                AssetVersionId = seed.VersionId,
                WorkspaceId = original.WorkspaceId,
                OperationId = body.operationId,
                ExpectedWorkspaceRevision = 1L,
                ExpectedCaseRevision = 1L,
                Rationale = body.rationale
            }));
            db.JsonMutationIdempotencyRecords.Add(new JsonMutationIdempotencyRecord
            {
                Id = Guid.NewGuid(),
                ActorUserId = original.OwnerUserId,
                OperationKind = ModerationOperationKinds.APPEAL_CREATE,
                OperationId = body.operationId,
                RequestDigest = requestDigest,
                ResultJson = System.Text.Json.JsonSerializer.Serialize(new { status = "CREATED", submissionId = appeal.Id, blockedReasonCode = (string?)null }),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            using HttpResponseMessage replay = await owner.PostAsJsonAsync(path, body);
            replay.StatusCode.Should().Be(HttpStatusCode.OK);
            (await replay.Content.ReadAsStringAsync()).Should().Contain(appeal.Id.ToString());
            using HttpResponseMessage changedReplay = await owner.PostAsJsonAsync(path, body with { rationale = "Different private rationale" });
            changedReplay.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await changedReplay.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT).And.NotContain("Different private rationale");
            using HttpResponseMessage duplicate = await owner.PostAsJsonAsync(path, body with { operationId = Guid.NewGuid() });
            duplicate.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await duplicate.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_APPEAL_EXISTS);
            using HttpResponseMessage queue = await moderator.GetAsync("/" + ApiRoutes.Moderation.QUEUE);
            queue.StatusCode.Should().Be(HttpStatusCode.OK);
            (await queue.Content.ReadAsStringAsync()).Should().NotContain(appeal.Id.ToString());
            var claimPath = "/" + ApiRoutes.Moderation.CLAIM.Replace("{id:guid}", appeal.Id.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage claim = await moderator.PostAsJsonAsync(claimPath, new { operationId = Guid.NewGuid(), expectedCaseRevision = 2 });
            claim.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await claim.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED);
            (await db.ModerationSubmissions.CountAsync(s => s.AssetVersionId == seed.VersionId)).Should().Be(2);
        }
    }

    private static object Basis(CaseSeed seed, long caseRevision = 1) => new
    {
        versionId = seed.VersionId,
        contentSha256 = CONTENT_SHA256,
        analysisReportId = seed.ReportId,
        declarationRevision = 1,
        materialMetadataRevision = 1,
        policyVersion = ReviewAnalysisPolicy.KEY,
        sellerEvidenceRevision = 0,
        caseRevision
    };

    private static object DecisionBody(CaseSeed seed, long caseRevision = 1, string outcome = "REQUEST_CHANGES", string rationale = "Please correct the evidence.") => new
    {
        operationId = Guid.NewGuid(),
        basis = Basis(seed, caseRevision),
        outcome,
        rationale,
        dispositions = new[] { new { findingId = seed.FindingId, disposition = "EXPLAINED", rationale = "Explained by the reviewer." } },
        manualPlanCompletionReference = (string?)null
    };

    private async Task<(HttpClient Client, string Username, Guid UserId)> RegisterModerator()
    {
        (HttpClient client, var username) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        Guid userId = (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.SingleAsync(u => u.Username == username)).Id;
        return (client, username, userId);
    }

    private async Task<int> CountDecisions(Guid submissionId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ModerationDecisionRecords.CountAsync(d => d.SubmissionId == submissionId);
    }

    private async Task<ModerationSubmissionState> StateOf(Guid submissionId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ModerationSubmissions.AsNoTracking()
            .SingleAsync(s => s.Id == submissionId)).State;
    }

    [Fact]
    public async Task Decision_WhenCallerCannotDecide_ShouldDenyWithNoStoreAndNoEffect()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        {
            (HttpClient holder, var holderName, _) = await RegisterModerator();
            (HttpClient other, _, _) = await RegisterModerator();
            using (holder)
            using (other)
            {
                CaseSeed seed = await SeedCase(ownerName, ModerationSubmissionState.IN_REVIEW, holderName);
                var path = DecisionPath(seed);

                using HttpResponseMessage anonymous = await fixture.Factory.CreateClient().PostAsJsonAsync(path, DecisionBody(seed));
                anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                anonymous.Headers.CacheControl!.NoStore.Should().BeTrue();

                using HttpResponseMessage ownerDenied = await owner.PostAsJsonAsync(path, DecisionBody(seed));
                ownerDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                ownerDenied.Headers.CacheControl!.NoStore.Should().BeTrue();

                (HttpClient admin, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.ADMIN);
                using (admin)
                using (HttpResponseMessage adminDenied = await admin.PostAsJsonAsync(path, DecisionBody(seed)))
                {
                    adminDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                    adminDenied.Headers.CacheControl!.NoStore.Should().BeTrue();
                }

                using HttpResponseMessage nonHolder = await other.PostAsJsonAsync(path, DecisionBody(seed));
                nonHolder.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                nonHolder.Headers.CacheControl!.NoStore.Should().BeTrue();
                (await nonHolder.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_NOT_HOLDER).And.Contain("Only the current reviewer can act on this submission.").And.NotContain(PRIVATE_DECISION_TEXT);

                (HttpClient foreign, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
                using (foreign)
                using (HttpResponseMessage foreignDenied = await foreign.PostAsJsonAsync(path, DecisionBody(seed)))
                { foreignDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden); }

                await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.Users.Where(u => u.Username == holderName).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
                }
                using HttpResponseMessage revoked = await holder.PostAsJsonAsync(path, DecisionBody(seed));
                revoked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                revoked.Headers.CacheControl!.NoStore.Should().BeTrue();

                (await StateOf(seed.SubmissionId)).Should().Be(ModerationSubmissionState.IN_REVIEW);
                (await CountDecisions(seed.SubmissionId)).Should().Be(0);
            }
        }
    }

    [Fact]
    public async Task Decision_WhenCaseIsUnclaimedOrOwnedByDeciderModerator_ShouldDenyWithoutEffects()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        {
            CaseSeed unclaimed = await SeedCase(ownerName);
            (HttpClient moderator, _, _) = await RegisterModerator();
            using (moderator)
            {
                using HttpResponseMessage notActive = await moderator.PostAsJsonAsync(DecisionPath(unclaimed), DecisionBody(unclaimed));
                notActive.StatusCode.Should().Be(HttpStatusCode.Conflict);
                notActive.Headers.CacheControl!.NoStore.Should().BeTrue();
                (await notActive.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_CASE_NOT_ACTIVE);
                (await CountDecisions(unclaimed.SubmissionId)).Should().Be(0);
            }

            (HttpClient selfModerator, var selfName) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
            using (selfModerator)
            {
                CaseSeed own = await SeedCase(selfName, ModerationSubmissionState.IN_REVIEW, selfName);
                using HttpResponseMessage selfReview = await selfModerator.PostAsJsonAsync(DecisionPath(own), DecisionBody(own));
                selfReview.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                selfReview.Headers.CacheControl!.NoStore.Should().BeTrue();
                (await selfReview.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_FORBIDDEN);
                (await StateOf(own.SubmissionId)).Should().Be(ModerationSubmissionState.IN_REVIEW);
                (await CountDecisions(own.SubmissionId)).Should().Be(0);
            }
        }
    }

    [Fact]
    public async Task Decision_WhenCaseIsMissing_ShouldReturnNotFound()
    {
        (HttpClient moderator, _, _) = await RegisterModerator();
        using (moderator)
        {
            var missing = new CaseSeed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
            using HttpResponseMessage response = await moderator.PostAsJsonAsync(DecisionPath(missing), DecisionBody(missing));
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_CASE_NOT_FOUND);
        }
    }

    [Theory]
    [InlineData("empty-rationale")]
    [InlineData("whitespace-rationale")]
    [InlineData("long-rationale")]
    [InlineData("missing-basis")]
    [InlineData("unknown-outcome")]
    [InlineData("too-many-dispositions")]
    [InlineData("null-dispositions")]
    [InlineData("empty-operation")]
    [InlineData("missing-outcome")]
    [InlineData("numeric-outcome-zero")]
    [InlineData("numeric-outcome-one")]
    [InlineData("null-outcome")]
    [InlineData("missing-disposition")]
    [InlineData("numeric-disposition")]
    [InlineData("oversize-raw-rationale")]
    [InlineData("missing-operation")]
    [InlineData("missing-revision")]
    public async Task Decision_WhenInputIsInvalid_ShouldReturnBadRequestWithNoStore(string invalid)
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        {
            (HttpClient holder, var holderName, _) = await RegisterModerator();
            using (holder)
            {
                CaseSeed seed = await SeedCase(ownerName, ModerationSubmissionState.IN_REVIEW, holderName);
                var body = invalid switch
                {
                    "empty-rationale" => DecisionBody(seed, rationale: ""),
                    "whitespace-rationale" => DecisionBody(seed, rationale: "   "),
                    "long-rationale" => DecisionBody(seed, rationale: new string('x', 2001)),
                    "unknown-outcome" => DecisionBody(seed, outcome: "APPROVE_ALL"),
                    "too-many-dispositions" => new
                    {
                        operationId = Guid.NewGuid(),
                        basis = Basis(seed),
                        outcome = "REQUEST_CHANGES",
                        rationale = "Please correct the evidence.",
                        dispositions = Enumerable.Range(0, 201).Select(_ => new { findingId = seed.FindingId, disposition = "EXPLAINED", rationale = "Explained." }).ToArray(),
                        manualPlanCompletionReference = (string?)null
                    },
                    "null-dispositions" => new
                    {
                        operationId = Guid.NewGuid(),
                        basis = Basis(seed),
                        outcome = "REQUEST_CHANGES",
                        rationale = "Please correct the evidence.",
                        dispositions = (object?)null,
                        manualPlanCompletionReference = (string?)null
                    },
                    "missing-operation" => new { basis = Basis(seed), outcome = "APPROVE", rationale = "Reviewed.", dispositions = Array.Empty<object>() },
                    "missing-revision" => new { operationId = Guid.NewGuid(), basis = new { versionId = seed.VersionId, contentSha256 = CONTENT_SHA256, analysisReportId = seed.ReportId, declarationRevision = 1, materialMetadataRevision = 1, policyVersion = ReviewAnalysisPolicy.KEY, sellerEvidenceRevision = 0 }, outcome = "APPROVE", rationale = "Reviewed.", dispositions = Array.Empty<object>() },
                    "empty-operation" => new
                    {
                        operationId = Guid.Empty,
                        basis = Basis(seed),
                        outcome = "REQUEST_CHANGES",
                        rationale = "Please correct the evidence.",
                        dispositions = Array.Empty<object>(),
                        manualPlanCompletionReference = (string?)null
                    },
                    "missing-outcome" => new
                    {
                        operationId = Guid.NewGuid(),
                        basis = Basis(seed),
                        rationale = "Please correct the evidence.",
                        dispositions = new[] { new { findingId = seed.FindingId, disposition = "EXPLAINED", rationale = "Explained." } },
                        manualPlanCompletionReference = (string?)null
                    },
                    "numeric-outcome-zero" => new
                    {
                        operationId = Guid.NewGuid(),
                        basis = Basis(seed),
                        outcome = 0,
                        rationale = "Please correct the evidence.",
                        dispositions = new[] { new { findingId = seed.FindingId, disposition = "EXPLAINED", rationale = "Explained." } },
                        manualPlanCompletionReference = (string?)null
                    },
                    "numeric-outcome-one" => new
                    {
                        operationId = Guid.NewGuid(),
                        basis = Basis(seed),
                        outcome = 1,
                        rationale = "Please correct the evidence.",
                        dispositions = new[] { new { findingId = seed.FindingId, disposition = "EXPLAINED", rationale = "Explained." } },
                        manualPlanCompletionReference = (string?)null
                    },
                    "null-outcome" => new
                    {
                        operationId = Guid.NewGuid(),
                        basis = Basis(seed),
                        outcome = (string?)null,
                        rationale = "Please correct the evidence.",
                        dispositions = new[] { new { findingId = seed.FindingId, disposition = "EXPLAINED", rationale = "Explained." } },
                        manualPlanCompletionReference = (string?)null
                    },
                    "missing-disposition" => new
                    {
                        operationId = Guid.NewGuid(),
                        basis = Basis(seed),
                        outcome = "APPROVE",
                        rationale = "Please correct the evidence.",
                        dispositions = new[] { new { findingId = seed.FindingId, rationale = "Explained." } },
                        manualPlanCompletionReference = (string?)null
                    },
                    "numeric-disposition" => new
                    {
                        operationId = Guid.NewGuid(),
                        basis = Basis(seed),
                        outcome = "APPROVE",
                        rationale = "Please correct the evidence.",
                        dispositions = new[] { new { findingId = seed.FindingId, disposition = 0, rationale = "Explained." } },
                        manualPlanCompletionReference = (string?)null
                    },
                    "oversize-raw-rationale" => DecisionBody(seed, rationale: "Please correct the evidence." + new string(' ', 4100)),
                    _ => new
                    {
                        operationId = Guid.NewGuid(),
                        outcome = "REQUEST_CHANGES",
                        rationale = "Please correct the evidence.",
                        dispositions = Array.Empty<object>(),
                        manualPlanCompletionReference = (string?)null
                    }
                };
                using HttpResponseMessage response = await holder.PostAsJsonAsync(DecisionPath(seed), body);
                response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                using var problem = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                problem.RootElement.GetProperty("status").GetInt32().Should().Be(400);
                problem.RootElement.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
                response.Headers.CacheControl!.NoStore.Should().BeTrue();
                (await StateOf(seed.SubmissionId)).Should().Be(ModerationSubmissionState.IN_REVIEW);
                (await CountDecisions(seed.SubmissionId)).Should().Be(0);
                await AssertNoPublicationEffects(seed);
            }
        }
    }

    [Fact]
    public async Task Decision_WhenBasisIsStaleOrCaseIsNotActive_ShouldReturnStandardConflicts()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        {
            (HttpClient holder, var holderName, _) = await RegisterModerator();
            using (holder)
            {
                CaseSeed stale = await SeedCase(ownerName, ModerationSubmissionState.IN_REVIEW, holderName);
                using HttpResponseMessage staleResponse = await holder.PostAsJsonAsync(DecisionPath(stale), DecisionBody(stale, caseRevision: 99));
                staleResponse.StatusCode.Should().Be(HttpStatusCode.Conflict);
                staleResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
                (await staleResponse.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_CASE_STALE);
                (await CountDecisions(stale.SubmissionId)).Should().Be(0);

                CaseSeed withdrawn = await SeedCase(ownerName, ModerationSubmissionState.WITHDRAWN);
                using HttpResponseMessage inactive = await holder.PostAsJsonAsync(DecisionPath(withdrawn), DecisionBody(withdrawn));
                inactive.StatusCode.Should().Be(HttpStatusCode.Conflict);
                (await inactive.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_CASE_NOT_ACTIVE);
                (await CountDecisions(withdrawn.SubmissionId)).Should().Be(0);
            }
        }
    }

    private async Task AssertNoPublicationEffects(CaseSeed seed)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Asset asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == db.AssetVersions.Where(v => v.Id == seed.VersionId).Select(v => v.AssetId).Single());
        (await db.PublicationSnapshots.AnyAsync(p => p.AssetId == asset.Id)).Should().BeFalse();
        asset.CurrentPublicationSnapshotId.Should().BeNull();
    }

    [Fact]
    public async Task Decision_WhenDecisionAlreadyExists_ShouldConflictWithoutLeakingDecisionText()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        {
            (HttpClient holder, var holderName, _) = await RegisterModerator();
            using (holder)
            {
                CaseSeed seed = await SeedCase(ownerName, ModerationSubmissionState.IN_REVIEW, holderName, withDecision: true);
                using HttpResponseMessage response = await holder.PostAsJsonAsync(DecisionPath(seed), DecisionBody(seed));
                response.StatusCode.Should().Be(HttpStatusCode.Conflict);
                response.Headers.CacheControl!.NoStore.Should().BeTrue();
                var body = await response.Content.ReadAsStringAsync();
                body.Should().Contain(ErrorCodes.ERR_MODERATION_DECISION_EXISTS);
                body.Should().NotContain(PRIVATE_DECISION_TEXT);
                (await CountDecisions(seed.SubmissionId)).Should().Be(1);
            }
        }
    }
}
