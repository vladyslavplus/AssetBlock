using System.Net;
using System.Net.Http.Json;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

/// <summary>
/// Moderator and owner-review endpoints against caller classes over HTTP. Each test seeds one case and runs the
/// classes in order: denials first, the allowed class last, because allowed calls change state. A denial must
/// leave the case, decision, snapshot, coverage, outbox and audit counts unchanged. Expected statuses come from the
/// controller attributes and handler checks: class-level Moderator role (token role replaced by the live persisted
/// role in JwtAuthenticationExtensions), then ownership and holder rules in the store (403), then not-found for
/// hidden owner resources (404). Withdrawal by the owner removes the case, so the owner-Moderator (same account)
/// sees 409. Seller write routes live in ReviewAccessMatrixSellerIntegrationTests.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class ReviewAccessMatrixIntegrationTests(IntegrationTestFixture fixture)
{
    private readonly ReviewAccessMatrix _matrix = new(fixture);

    [Fact]
    public async Task ClaimEndpoint_WhenCallerClassVaries_ShouldEnforceModeratorRoleOwnershipAndLiveRole()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.OK)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "claim",
                (client, c) => ModerationDecisionFixture.PostClaimAsync(client, c.SubmissionId, Guid.NewGuid(), 1),
                moderatorRoute: true);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task DecisionEndpoint_WhenCallerClassVaries_ShouldDecideOnlyForTheLiveClaimHolder()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName);
            await ReviewAccessMatrix.ClaimForOtherAsync(callers, seed);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.OK)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "decision",
                async (client, c) =>
                {
                    var basis = await _matrix.ReadBasisAsync(c);
                    return await ModerationDecisionFixture.PostDecisionAsync(client, c.SubmissionId, ModerationDecisionFixture.DecisionBody(Guid.NewGuid(), basis, "APPROVE"));
                },
                moderatorRoute: true);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task ReleaseEndpoint_WhenCallerClassVaries_ShouldReleaseOnlyForTheLiveClaimHolder()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName);
            await ReviewAccessMatrix.ClaimForOtherAsync(callers, seed);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.OK)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "release",
                async (client, c) =>
                {
                    ReviewCaseSnapshot snapshot = await _matrix.ReadSnapshotAsync(c);
                    return await client.PostAsJsonAsync(
                        ReviewAccessMatrix.RouteUri(ApiRoutes.Moderation.RELEASE, ("{id:guid}", c.SubmissionId)),
                        new { operationId = Guid.NewGuid(), expectedCaseRevision = snapshot.CaseRevision });
                },
                moderatorRoute: true);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task CaseEndpoint_WhenCallerClassVaries_ShouldHideCaseFromOwnerAndNonModerators()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName);
            await ReviewAccessMatrix.ClaimForOtherAsync(callers, seed);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.OK)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "case",
                (client, c) => client.GetAsync(ReviewAccessMatrix.RouteUri(ApiRoutes.Moderation.SUBMISSION_BY_ID, ("{id:guid}", c.SubmissionId))),
                moderatorRoute: true);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task QueueEndpoint_WhenCallerClassVaries_ShouldListForeignCasesOnlyToTheLiveModerator()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.OK),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.OK)
            };
            Uri queueUri = ReviewAccessMatrix.RouteUri(ApiRoutes.Moderation.QUEUE);
            await _matrix.RunStepsAsync(callers, seed, steps, "queue",
                (client, _) => client.GetAsync(queueUri),
                moderatorRoute: true);

            // Owner-Moderator queue excludes the own asset's case on every page; a different Moderator lists it on some page.
            HashSet<string> ownerQueue = await ReviewAccessMatrix.ReadAllQueueSubmissionIdsAsync(callers.Clients[ReviewCaller.OwnerModerator]);
            ownerQueue.Should().NotContain(seed.SubmissionId.ToString(), "owner-Moderator must not queue own case");
            HashSet<string> otherQueue = await ReviewAccessMatrix.ReadAllQueueSubmissionIdsAsync(callers.Clients[ReviewCaller.OtherModerator]);
            otherQueue.Should().Contain(seed.SubmissionId.ToString(), "other Moderator must queue the case");
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task CaseEvidenceEndpoint_WhenCallerClassVaries_ShouldServeDeclarationOnlyToForeignModerators()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Forbidden, AuditDelta: 1),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.OK)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "case-evidence",
                (client, c) => client.GetAsync(ReviewAccessMatrix.RouteUri(
                    ApiRoutes.Moderation.CASE_EVIDENCE,
                    ("{id:guid}", c.SubmissionId),
                    ("{kind}", "DECLARATION"))),
                moderatorRoute: true);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task CoverageRequestListEndpoint_WhenCallerClassVaries_ShouldRequireOnlyTheLiveModeratorRole()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName, partialCoverage: true);
            CoverageReviewSaved request = await ReviewAccessMatrix.CreateCoverageRequestAsync(callers, seed);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.OK),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.OK)
            };
            Uri listUri = ReviewAccessMatrix.RouteUri(ApiRoutes.Moderation.COVERAGE_REQUESTS);
            await _matrix.RunStepsAsync(callers, seed, steps, "coverage-requests",
                (client, _) => client.GetAsync(listUri),
                moderatorRoute: true);

            // Owner-Moderator never sees a request filed for its own asset on any page; a different Moderator sees it on some page.
            HashSet<string> ownerRequests = await ReviewAccessMatrix.ReadAllCoverageRequestIdsAsync(callers.Clients[ReviewCaller.OwnerModerator]);
            ownerRequests.Should().NotContain(request.RequestId.ToString(), "owner-Moderator must not see own asset request");
            HashSet<string> otherRequests = await ReviewAccessMatrix.ReadAllCoverageRequestIdsAsync(callers.Clients[ReviewCaller.OtherModerator]);
            otherRequests.Should().Contain(request.RequestId.ToString(), "other Moderator must see the request");
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task CoverageEvidenceEndpoint_WhenCallerClassVaries_ShouldHideRequestEvidenceWithoutStoredInventory()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName, partialCoverage: true);
            CoverageReviewSaved request = await ReviewAccessMatrix.CreateCoverageRequestAsync(callers, seed);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Forbidden, AuditDelta: 1),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.NotFound, AuditDelta: 1)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "coverage-evidence",
                (client, _) => client.GetAsync(ReviewAccessMatrix.RouteUri(
                    ApiRoutes.Moderation.COVERAGE_EVIDENCE,
                    ("{id:guid}", request.RequestId),
                    ("{kind}", "COVERAGE_INVENTORY"))),
                moderatorRoute: true);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task CoverageAssessmentEndpoint_WhenCallerClassVaries_ShouldAssessOnlyForeignRequests()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName, partialCoverage: true);
            CoverageReviewSaved request = await ReviewAccessMatrix.CreateCoverageRequestAsync(callers, seed);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.OK)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "coverage-assessment",
                (client, _) => client.PostAsJsonAsync(
                    ReviewAccessMatrix.RouteUri(ApiRoutes.Moderation.COVERAGE_ASSESSMENT, ("{id:guid}", request.RequestId)),
                    new CoverageAssessmentRequest(
                        Guid.NewGuid(),
                        request.RequestRevision,
                        request.TupleDigest,
                        CoverageAssessmentOutcome.BLOCKED,
                        ["Markdown"],
                        [],
                        false,
                        "Manual review plan for the access matrix.",
                        "manual-review/access-matrix",
                        "Blocked assessment rationale for the access matrix.")),
                moderatorRoute: true);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task WithdrawEndpoint_WhenCallerClassVaries_ShouldAllowOnlyTheAuthorAndOnlyOnce()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.OK),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Conflict)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "withdraw",
                (client, c) => client.PostAsJsonAsync(
                    ReviewAccessMatrix.RouteUri(ApiRoutes.Moderation.SUBMISSION_WITHDRAW, ("{id:guid}", c.SubmissionId)),
                    new { operationId = Guid.NewGuid(), expectedCaseRevision = 1L }),
                moderatorRoute: false);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task OwnerReviewEndpoint_WhenCallerClassVaries_ShouldServeOnlyTheAuthorAndHideOthers()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.NotFound),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.NotFound),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.NotFound),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.NotFound),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.OK),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.OK)
            };
            Uri reviewUri = ReviewAccessMatrix.RouteUri(
                ApiRoutes.Users.BASE + "/" + ApiRoutes.Users.ME_ASSET_VERSION_REVIEW,
                ("{assetVersionId:guid}", seed.VersionId));
            await _matrix.RunStepsAsync(callers, seed, steps, "owner-review",
                (client, _) => client.GetAsync(reviewUri),
                moderatorRoute: false);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }
}
