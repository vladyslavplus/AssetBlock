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
