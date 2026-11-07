using System.Net;
using System.Net.Http.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

/// <summary>
/// Seller write and read routes of the review loop against caller classes over HTTP. The owner account is the
/// only class that reaches the store past the ownership check. Owner-Moderator (same account after promotion)
/// also passes that check because CanActAsOwner admits the Moderator role. Denied calls must leave the case,
/// workspace, response, evidence, coverage, submission, outbox and audit counts unchanged. Submission routes
/// map every blocked outcome to 409, so there the problem code (ownership or analysis gate) carries the difference.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class ReviewAccessMatrixSellerIntegrationTests(IntegrationTestFixture fixture)
{
    private readonly ReviewAccessMatrix _matrix = new(fixture);

    [Fact]
    public async Task SellerCoverageRequestEndpoint_WhenCallerClassVaries_ShouldFileOnlyForTheAuthor()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName, partialCoverage: true);
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
            await _matrix.RunStepsAsync(callers, seed, steps, "seller-coverage-request",
                async (client, c) =>
                {
                    ReviewSellerRevisions revisions = await _matrix.ReadSellerRevisionsAsync(c);
                    return await client.PostAsJsonAsync(
                        ReviewAccessMatrix.RouteUri(ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_COVERAGE_REVIEWS, ("{id:guid}", c.AssetId), ("{versionId:guid}", c.VersionId)),
                        new CoverageReviewCreateRequest(Guid.NewGuid(), c.WorkspaceId, c.ReportId, revisions.WorkspaceRevision));
                },
                moderatorRoute: false);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task SellerResponsesReadEndpoint_WhenCallerClassVaries_ShouldServeOnlyTheAuthor()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName, withFinding: true);
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
            await _matrix.RunStepsAsync(callers, seed, steps, "seller-responses-read",
                (client, c) => client.GetAsync(ReviewAccessMatrix.RouteUri(
                    ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_RESPONSES,
                    ("{id:guid}", c.AssetId),
                    ("{versionId:guid}", c.VersionId))),
                moderatorRoute: false);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task SellerResponsesWriteEndpoint_WhenCallerClassVaries_ShouldSaveOnlyForTheAuthor()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName, withFinding: true);
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Forbidden),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.OK),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.OK)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "seller-responses-write",
                async (client, c) =>
                {
                    ReviewSellerRevisions revisions = await _matrix.ReadSellerRevisionsAsync(c);
                    return await client.PostAsJsonAsync(
                        ReviewAccessMatrix.RouteUri(ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_RESPONSES, ("{id:guid}", c.AssetId), ("{versionId:guid}", c.VersionId)),
                        new SellerResponseRequest(
                            c.WorkspaceId,
                            c.ReportId,
                            c.FindingId,
                            revisions.WorkspaceRevision,
                            revisions.EvidenceRevision,
                            Guid.NewGuid(),
                            SellerResponseType.INDEPENDENT_IMPLEMENTATION,
                            "Independent implementation fixture text."));
                },
                moderatorRoute: false);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task SellerEvidenceEndpoint_WhenCallerClassVaries_ShouldEditOnlyForTheAuthor()
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
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.OK)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "seller-evidence",
                async (client, c) =>
                {
                    ReviewSellerRevisions revisions = await _matrix.ReadSellerRevisionsAsync(c);
                    return await client.PutAsJsonAsync(
                        ReviewAccessMatrix.RouteUri(ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_EVIDENCE, ("{id:guid}", c.AssetId), ("{versionId:guid}", c.VersionId)),
                        new SellerEvidenceEditRequest(
                            c.WorkspaceId,
                            c.ReportId,
                            revisions.WorkspaceRevision,
                            revisions.EvidenceRevision,
                            Guid.NewGuid(),
                            "Owner evidence text for the access matrix."));
                },
                moderatorRoute: false);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task SellerSubmissionsEndpoint_WhenCallerClassVaries_ShouldNeverOpenASecondCase()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        try
        {
            ReviewCase seed = await _matrix.SeedCaseAsync(callers.OwnerName);
            // Every class gets 409. The problem code separates non-owners (ownership denial, checked first in the store)
            // from the owner, who passes ownership and then stops on the seeded report's analysis availability. The
            // active-case check comes after that gate and is not reached by this seed.
            var steps = new ReviewStep[]
            {
                new ReviewStep(ReviewCaller.Anonymous, HttpStatusCode.Unauthorized),
                new ReviewStep(ReviewCaller.Stranger, HttpStatusCode.Conflict, ExpectedCode: ErrorCodes.ERR_FORBIDDEN),
                new ReviewStep(ReviewCaller.OtherModerator, HttpStatusCode.Conflict, ExpectedCode: ErrorCodes.ERR_FORBIDDEN),
                new ReviewStep(ReviewCaller.Admin, HttpStatusCode.Conflict, ExpectedCode: ErrorCodes.ERR_FORBIDDEN),
                new ReviewStep(ReviewCaller.RevokedModerator, HttpStatusCode.Conflict, ExpectedCode: ErrorCodes.ERR_FORBIDDEN),
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Conflict, ExpectedCode: ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Conflict, ExpectedCode: ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "seller-submissions",
                async (client, c) =>
                {
                    ReviewSellerRevisions revisions = await _matrix.ReadSellerRevisionsAsync(c);
                    ReviewCaseSnapshot snapshot = await _matrix.ReadSnapshotAsync(c);
                    return await client.PostAsJsonAsync(
                        ReviewAccessMatrix.RouteUri(ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_SUBMISSIONS, ("{id:guid}", c.AssetId), ("{versionId:guid}", c.VersionId)),
                        new
                        {
                            workspaceId = c.WorkspaceId,
                            expectedWorkspaceRevision = revisions.WorkspaceRevision,
                            expectedCaseRevision = snapshot.CaseRevision,
                            operationId = Guid.NewGuid()
                        });
                },
                moderatorRoute: false);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task SellerAppealEndpoint_WhenCallerClassVaries_ShouldAppealOnlyForTheAuthor()
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
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Conflict),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Conflict)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "seller-appeal",
                async (client, c) =>
                {
                    ReviewSellerRevisions revisions = await _matrix.ReadSellerRevisionsAsync(c);
                    ReviewCaseSnapshot snapshot = await _matrix.ReadSnapshotAsync(c);
                    return await client.PostAsJsonAsync(
                        ReviewAccessMatrix.RouteUri(ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_APPEALS, ("{id:guid}", c.AssetId), ("{versionId:guid}", c.VersionId)),
                        new
                        {
                            workspaceId = c.WorkspaceId,
                            expectedWorkspaceRevision = revisions.WorkspaceRevision,
                            expectedCaseRevision = snapshot.CaseRevision,
                            operationId = Guid.NewGuid(),
                            rationale = "Appeal rationale for the access matrix."
                        });
                },
                moderatorRoute: false);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task SellerAnalysisFindingsEndpoint_WhenCallerClassVaries_ShouldServeOnlyTheAuthor()
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
            await _matrix.RunStepsAsync(callers, seed, steps, "seller-analysis-findings",
                (client, c) => client.GetAsync(ReviewAccessMatrix.RouteUri(
                    ApiRoutes.Users.BASE + "/" + ApiRoutes.Users.ME_ASSET_VERSION_ANALYSIS_FINDINGS,
                    ("{assetVersionId:guid}", c.VersionId),
                    ("{reportId:guid}", c.ReportId))),
                moderatorRoute: false);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    [Fact]
    public async Task SellerAnalysisRetryEndpoint_WhenCallerClassVaries_ShouldRetryOnlyForTheAuthor()
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
                new ReviewStep(ReviewCaller.OwnerUser, HttpStatusCode.Conflict),
                new ReviewStep(ReviewCaller.OwnerModerator, HttpStatusCode.Conflict)
            };
            await _matrix.RunStepsAsync(callers, seed, steps, "seller-analysis-retry",
                async (client, c) =>
                {
                    ReviewSellerRevisions revisions = await _matrix.ReadSellerRevisionsAsync(c);
                    CodeAnalysisRun run = await _matrix.ReadRunAsync(c);
                    return await client.PostAsJsonAsync(
                        ReviewAccessMatrix.RouteUri(
                            ApiRoutes.Users.BASE + "/" + ApiRoutes.Users.ME_ASSET_VERSION_ANALYSIS_RETRY,
                            ("{assetId:guid}", c.AssetId),
                            ("{assetVersionId:guid}", c.VersionId)),
                        new
                        {
                            operationId = Guid.NewGuid(),
                            expectedRunId = run.Id,
                            expectedWorkspaceRevision = revisions.WorkspaceRevision,
                            expectedPolicyDigest = run.PolicyDigest,
                            expectedInputDigest = run.InputDigest
                        });
                },
                moderatorRoute: false);
        }
        finally
        {
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }
}
