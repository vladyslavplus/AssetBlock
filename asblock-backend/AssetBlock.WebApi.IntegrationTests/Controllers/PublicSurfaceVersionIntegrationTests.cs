using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class PublicSurfaceVersionIntegrationTests(IntegrationTestFixture fixture)
{
    private const string PENDING_TITLE = "Pending draft title sentinel";
    private const string ASSETS_BASE = "/" + ApiRoutes.Assets.BASE;

    [Fact]
    public async Task SellerReview_WhenCaseIsPending_ShouldSerializeSafeAppealFields()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient stranger, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        using (stranger)
        {
            Seeded seeded = await SeedApprovedWithPendingVersion(ownerName, ModerationSubmissionState.SUBMITTED, null);
            var path = "/" + ApiRoutes.Users.BASE + "/" + ApiRoutes.Users.ME_ASSET_VERSION_REVIEW.Replace("{assetVersionId:guid}", seeded.PendingVersionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage response = await owner.GetAsync(new Uri(path, UriKind.Relative));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var json = await response.Content.ReadAsStringAsync();
            using var body = System.Text.Json.JsonDocument.Parse(json);
            body.RootElement.GetProperty("canAppeal").GetBoolean().Should().BeFalse();
            body.RootElement.GetProperty("appeal").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
            System.Text.Json.JsonElement active = body.RootElement.GetProperty("activeSubmission");
            active.GetProperty("submissionId").GetGuid().Should().NotBe(Guid.Empty);
            active.GetProperty("kind").GetString().Should().Be("STANDARD");
            active.GetProperty("state").GetString().Should().Be("SUBMITTED");
            active.GetProperty("caseRevision").GetInt64().Should().Be(1);
            System.Text.Json.JsonElement history = body.RootElement.GetProperty("history");
            history.GetArrayLength().Should().Be(1);
            history[0].GetProperty("submissionId").GetGuid().Should().Be(active.GetProperty("submissionId").GetGuid());
            history[0].GetProperty("kind").GetString().Should().Be("STANDARD");
            json.Should().NotContain("claimHolder").And.NotContain("moderatorUserId").And.NotContain("moderatorId").And.NotContain("disposition")
                .And.NotContain("score").And.NotContain("exclusion");
            using HttpResponseMessage denied = await stranger.GetAsync(new Uri(path, UriKind.Relative));
            denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    private sealed record Seeded(Guid AssetId, Guid ApprovedVersionId, Guid PendingVersionId, string ApprovedTitle, string Token);

    private async Task<Seeded> SeedApprovedWithPendingVersion(string ownerUsername, ModerationSubmissionState pendingState, string? moderatorUsername)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User owner = await db.Users.SingleAsync(u => u.Username == ownerUsername);
        Guid? holderId = moderatorUsername is null ? null : (await db.Users.SingleAsync(u => u.Username == moderatorUsername)).Id;
        var token = "PublicSurface" + Guid.NewGuid().ToString("N");
        var approvedTitle = token + " approved title";
        var category = new Category { Id = Guid.NewGuid(), Name = "Public surface", Slug = "public-surface-" + Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
        var asset = new Asset { Id = Guid.NewGuid(), AuthorId = owner.Id, CategoryId = category.Id, Title = PENDING_TITLE, Description = "", Price = 1m, CreatedAt = DateTimeOffset.UtcNow };
        AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        AssetVersion approved = NewVersion(asset.Id, 1, new string('a', 64), license, AssetVersionProcessingStatus.READY);
        AssetVersion pending = NewVersion(asset.Id, 2, new string('b', 64), license, AssetVersionProcessingStatus.READY);
        db.Categories.Add(category);
        db.Assets.Add(asset);
        db.AssetVersions.Add(approved);
        db.AssetVersions.Add(pending);
        await db.SaveChangesAsync();
        await TrustedPublicationFixture.AttachTrustedApprovedPublication(db, asset, approved, owner, category, approvedTitle);
        var workspace = new AssetDraftWorkspace { Id = Guid.NewGuid(), AssetId = asset.Id, AssetVersionId = pending.Id, WorkspaceVersionScopeKey = pending.Id, ScopeId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        db.AssetDraftWorkspaces.Add(workspace);
        db.ModerationSubmissions.Add(new ModerationSubmission
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = pending.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = pending.Id,
            OwnerUserId = owner.Id,
            ContentSha256 = pending.ContentSha256,
            DeclarationRevision = 1,
            MaterialMetadataRevision = 1,
            SellerEvidenceRevision = 0,
            SellerEvidenceDigest = new string('e', 64),
            PolicyVersion = ReviewAnalysisPolicy.KEY,
            State = pendingState,
            ClaimHolderId = pendingState == ModerationSubmissionState.IN_REVIEW ? holderId : null,
            ClaimedAt = pendingState == ModerationSubmissionState.IN_REVIEW ? DateTimeOffset.UtcNow : null,
            CaseRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        asset.Title = PENDING_TITLE;
        await db.SaveChangesAsync();
        return new(asset.Id, approved.Id, pending.Id, approvedTitle, token);
    }

    private static AssetVersion NewVersion(Guid assetId, int number, string sha, AssetLicenseTemplate license, AssetVersionProcessingStatus status) => new()
    {
        Id = Guid.NewGuid(),
        AssetId = assetId,
        VersionNumber = number,
        IsCurrent = number == 1,
        StorageKey = "public-surface/" + Guid.NewGuid().ToString("N") + "/source.bin",
        FileName = "source.zip",
        ContentLength = 1,
        ContentSha256 = sha,
        ReleaseNotes = "Fixture",
        LicenseCode = license.Code,
        LicenseTemplateVersion = license.TemplateVersion,
        LicenseDisplayName = license.DisplayName,
        LicenseTerms = license.TermsPlainText,
        ProcessingStatus = status,
        ProcessingUpdatedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Theory]
    [InlineData(ModerationSubmissionState.SUBMITTED)]
    [InlineData(ModerationSubmissionState.IN_REVIEW)]
    [InlineData(ModerationSubmissionState.CHANGES_REQUESTED)]
    [InlineData(ModerationSubmissionState.REJECTED)]
    public async Task PendingVersion_WhenNotApproved_ShouldStayHiddenFromAnonymousAndBuyerSurfaces(ModerationSubmissionState pendingState)
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient moderator, var moderatorName) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        using (owner)
        using (moderator)
        {
            Seeded seeded = await SeedApprovedWithPendingVersion(ownerName, pendingState, pendingState == ModerationSubmissionState.IN_REVIEW ? moderatorName : null);
            (HttpClient buyer, var buyerUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
            (HttpClient checkoutBuyer, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
            (HttpClient stranger, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
            using (buyer)
            using (checkoutBuyer)
            using (stranger)
            using (HttpClient anonymous = fixture.Factory.CreateClient())
            {
                IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
                Guid buyerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, buyerUsername);
                await AssetVersionsSeed.SeedPurchaseAsync(scopeFactory, buyerId, seeded.AssetId, seeded.ApprovedVersionId);
                var plaintext = "approved v1 entitled bytes"u8.ToArray();
                await SeedEncryptedContentAsync(await AssetVersionsSeed.GetVersionStorageKeyAsync(scopeFactory, seeded.ApprovedVersionId), plaintext);

                var catalogUrl = ASSETS_BASE + ApiRoutes.Assets.LIST + "?page=1&pageSize=20&search=" + Uri.EscapeDataString(seeded.Token);
                var assetUrl = ASSETS_BASE + "/" + ApiRoutes.Assets.ID.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal);
                var versionsUrl = ASSETS_BASE + "/" + ApiRoutes.Assets.VERSIONS.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal);
                var pendingDownload = ASSETS_BASE + "/" + ApiRoutes.Assets.VERSION_DOWNLOAD.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal)
                    .Replace("{versionId:guid}", seeded.PendingVersionId.ToString(), StringComparison.Ordinal);
                var approvedDownload = ASSETS_BASE + "/" + ApiRoutes.Assets.VERSION_DOWNLOAD.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal)
                    .Replace("{versionId:guid}", seeded.ApprovedVersionId.ToString(), StringComparison.Ordinal);
                var assetDownload = ASSETS_BASE + "/" + ApiRoutes.Assets.DOWNLOAD.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal);

                foreach (HttpClient client in new[] { anonymous, buyer, stranger })
                {
                    using HttpResponseMessage catalog = await client.GetAsync(new Uri(catalogUrl, UriKind.Relative));
                    catalog.StatusCode.Should().Be(HttpStatusCode.OK);
                    var catalogBody = await catalog.Content.ReadAsStringAsync();
                    catalogBody.Should().Contain(seeded.AssetId.ToString());
                    catalogBody.Should().Contain(seeded.ApprovedTitle);
                    catalogBody.Should().NotContain(PENDING_TITLE);
                    catalogBody.Should().NotContain(seeded.PendingVersionId.ToString());

                    using HttpResponseMessage detail = await client.GetAsync(new Uri(assetUrl, UriKind.Relative));
                    detail.StatusCode.Should().Be(HttpStatusCode.OK);
                    var detailBody = await detail.Content.ReadAsStringAsync();
                    detailBody.Should().Contain(seeded.ApprovedTitle);
                    detailBody.Should().NotContain(PENDING_TITLE);
                    detailBody.Should().NotContain(seeded.PendingVersionId.ToString());

                    using HttpResponseMessage versions = await client.GetAsync(new Uri(versionsUrl, UriKind.Relative));
                    versions.StatusCode.Should().Be(HttpStatusCode.OK);
                    var versionsBody = await versions.Content.ReadAsStringAsync();
                    versionsBody.Should().Contain(seeded.ApprovedVersionId.ToString());
                    versionsBody.Should().NotContain(seeded.PendingVersionId.ToString());
                }

                // Anonymous callers are rejected by authentication before any version is resolved.
                using HttpResponseMessage anonymousPending = await anonymous.GetAsync(new Uri(pendingDownload, UriKind.Relative));
                anonymousPending.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                using HttpResponseMessage anonymousApproved = await anonymous.GetAsync(new Uri(approvedDownload, UriKind.Relative));
                anonymousApproved.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

                // A buyer without a purchase is refused by the purchase check, not by version resolution.
                using HttpResponseMessage strangerApproved = await stranger.GetAsync(new Uri(approvedDownload, UriKind.Relative));
                strangerApproved.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using HttpResponseMessage strangerPending = await stranger.GetAsync(new Uri(pendingDownload, UriKind.Relative));
                strangerPending.StatusCode.Should().Be(HttpStatusCode.Forbidden);

                // The entitled buyer gets exactly the approved v1 bytes, both explicitly and through the versionless route.
                using HttpResponseMessage buyerApproved = await buyer.GetAsync(new Uri(approvedDownload, UriKind.Relative));
                buyerApproved.StatusCode.Should().Be(HttpStatusCode.OK);
                (await buyerApproved.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo(plaintext);
                using HttpResponseMessage buyerVersionless = await buyer.GetAsync(new Uri(assetDownload, UriKind.Relative));
                buyerVersionless.StatusCode.Should().Be(HttpStatusCode.OK);
                (await buyerVersionless.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo(plaintext);

                // The pending version is not approved, so the purchase path must answer NOT_FOUND exactly, never 200.
                using HttpResponseMessage buyerPending = await buyer.GetAsync(new Uri(pendingDownload, UriKind.Relative));
                buyerPending.StatusCode.Should().Be(HttpStatusCode.NotFound);

                // Checkout is asset-scoped and resolves only the approved current publication, never the pending version.
                using HttpResponseMessage checkout = await checkoutBuyer.PostAsJsonAsync(
                    "/api/payments/" + ApiRoutes.Payments.CHECKOUT,
                    new CreateCheckoutRequest(seeded.AssetId));
                checkout.StatusCode.Should().Be(HttpStatusCode.OK, await checkout.Content.ReadAsStringAsync());
                CreateCheckoutSessionResponse? checkoutResult = await checkout.Content.ReadFromJsonAsync<CreateCheckoutSessionResponse>(IntegrationTestAuth.JsonOptions);
                checkoutResult.Should().NotBeNull();
                await using AsyncServiceScope checkoutScope = fixture.Factory.Services.CreateAsyncScope();
                ApplicationDbContext checkoutDb = checkoutScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                (await checkoutDb.CheckoutIntentItems.AsNoTracking().Where(i => i.CheckoutIntentId == checkoutResult!.CheckoutIntentId)
                    .Select(i => i.AssetVersionId).SingleAsync()).Should().Be(seeded.ApprovedVersionId);
            }
            await using AsyncServiceScope verify = fixture.Factory.Services.CreateAsyncScope();
            ApplicationDbContext db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.AssetVersionId == seeded.PendingVersionId)).State
                .Should().Be(pendingState);
            (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == seeded.AssetId)).CurrentPublicationSnapshotId.Should().NotBeNull();
        }
    }

    [Theory]
    [InlineData("REQUEST_CHANGES", ModerationSubmissionState.CHANGES_REQUESTED)]
    [InlineData("REJECT", ModerationSubmissionState.REJECTED)]
    public async Task PublicResponses_WhenPendingDecisionDoesNotApprove_ShouldRemainByteIdentical(string outcome, ModerationSubmissionState expectedState)
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient reviewer, var reviewerName) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        using (owner)
        using (reviewer)
        {
            Seeded seeded = await SeedApprovedWithPendingVersion(ownerName, ModerationSubmissionState.SUBMITTED, null);
            Guid submissionId;
            Guid reportId;
            await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
            {
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                ModerationSubmission submission = await db.ModerationSubmissions.SingleAsync(s => s.AssetVersionId == seeded.PendingVersionId);
                reportId = await ModerationDecisionFixture.PrepareDecisionReadySubmission(db, submission);
                submissionId = submission.Id;
            }

            var catalogUrl = ASSETS_BASE + ApiRoutes.Assets.LIST + "?page=1&pageSize=20&search=" + Uri.EscapeDataString(seeded.Token);
            var assetUrl = ASSETS_BASE + "/" + ApiRoutes.Assets.ID.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal);
            var versionsUrl = ASSETS_BASE + "/" + ApiRoutes.Assets.VERSIONS.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal);
            using HttpClient anonymous = fixture.Factory.CreateClient();
            var before = await ReadRawPublicResponses(anonymous, catalogUrl, assetUrl, versionsUrl);

            var claimPath = "/" + ApiRoutes.Moderation.CLAIM.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage claimed = await reviewer.PostAsJsonAsync(claimPath, new { operationId = Guid.NewGuid(), expectedCaseRevision = 1 });
            claimed.StatusCode.Should().Be(HttpStatusCode.OK, await claimed.Content.ReadAsStringAsync());
            var basis = await ReadDecisionBasis(submissionId, reportId);
            var decisionPath = "/" + ApiRoutes.Moderation.DECISION.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage decision = await reviewer.PostAsJsonAsync(decisionPath, new
            {
                operationId = Guid.NewGuid(),
                basis,
                outcome,
                rationale = "Reviewed for the requested outcome.",
                dispositions = Array.Empty<object>(),
                manualPlanCompletionReference = (string?)null
            });
            decision.StatusCode.Should().Be(HttpStatusCode.OK, await decision.Content.ReadAsStringAsync());
            await using AsyncServiceScope verify = fixture.Factory.Services.CreateAsyncScope();
            (await verify.ServiceProvider.GetRequiredService<ApplicationDbContext>().ModerationSubmissions.AsNoTracking()
                .Where(s => s.Id == submissionId).Select(s => s.State).SingleAsync()).Should().Be(expectedState);

            var after = await ReadRawPublicResponses(anonymous, catalogUrl, assetUrl, versionsUrl);
            after.Should().Equal(before);
        }
    }

    [Fact]
    public async Task DecisionRoute_WhenApprovingPendingV2_ShouldPromoteItAndPreserveBuyerAccessToV1()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient reviewer, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        (HttpClient buyer, var buyerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        using (reviewer)
        using (buyer)
        {
            Seeded seeded = await SeedApprovedWithPendingVersion(ownerName, ModerationSubmissionState.SUBMITTED, null);
            IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
            Guid buyerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, buyerName);
            await AssetVersionsSeed.SeedPurchaseAsync(scopeFactory, buyerId, seeded.AssetId, seeded.ApprovedVersionId);
            var v1Bytes = "approved v1 entitled bytes"u8.ToArray();
            var v2Bytes = "approved v2 entitled bytes"u8.ToArray();
            await SeedEncryptedContentAsync(await AssetVersionsSeed.GetVersionStorageKeyAsync(scopeFactory, seeded.ApprovedVersionId), v1Bytes);
            await SeedEncryptedContentAsync(await AssetVersionsSeed.GetVersionStorageKeyAsync(scopeFactory, seeded.PendingVersionId), v2Bytes);

            Guid submissionId;
            Guid reportId;
            await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
            {
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                ModerationSubmission submission = await db.ModerationSubmissions.SingleAsync(s => s.AssetVersionId == seeded.PendingVersionId);
                reportId = await ModerationDecisionFixture.PrepareDecisionReadySubmission(db, submission);
                submissionId = submission.Id;
            }
            var claimPath = "/" + ApiRoutes.Moderation.CLAIM.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage claimed = await reviewer.PostAsJsonAsync(claimPath, new { operationId = Guid.NewGuid(), expectedCaseRevision = 1 });
            claimed.StatusCode.Should().Be(HttpStatusCode.OK, await claimed.Content.ReadAsStringAsync());
            var basis = await ReadDecisionBasis(submissionId, reportId);
            var decisionPath = "/" + ApiRoutes.Moderation.DECISION.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage decision = await reviewer.PostAsJsonAsync(decisionPath, new
            {
                operationId = Guid.NewGuid(),
                basis,
                outcome = "APPROVE",
                rationale = "Approved after human review.",
                dispositions = Array.Empty<object>(),
                manualPlanCompletionReference = (string?)null
            });
            decision.StatusCode.Should().Be(HttpStatusCode.OK, await decision.Content.ReadAsStringAsync());
            using var decisionJson = System.Text.Json.JsonDocument.Parse(await decision.Content.ReadAsStringAsync());
            decisionJson.RootElement.GetProperty("promoted").GetBoolean().Should().BeTrue();

            Guid approvedV2SnapshotId;
            await using (AsyncServiceScope verify = fixture.Factory.Services.CreateAsyncScope())
            {
                ApplicationDbContext db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                Asset asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == seeded.AssetId);
                approvedV2SnapshotId = decisionJson.RootElement.GetProperty("publicationSnapshotId").GetGuid();
                asset.CurrentPublicationSnapshotId.Should().Be(approvedV2SnapshotId);
                (await db.PublicationSnapshots.AsNoTracking().Where(p => p.Id == asset.CurrentPublicationSnapshotId)
                    .Select(p => p.AssetVersionId).SingleAsync()).Should().Be(seeded.PendingVersionId);
            }

            var detailUrl = ASSETS_BASE + "/" + ApiRoutes.Assets.ID.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage detail = await fixture.Factory.CreateClient().GetAsync(new Uri(detailUrl, UriKind.Relative));
            detail.StatusCode.Should().Be(HttpStatusCode.OK);
            (await detail.Content.ReadAsStringAsync()).Should().Contain("Pending public title");
            var v1Download = ASSETS_BASE + "/" + ApiRoutes.Assets.VERSION_DOWNLOAD.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal)
                .Replace("{versionId:guid}", seeded.ApprovedVersionId.ToString(), StringComparison.Ordinal);
            var v2Download = ASSETS_BASE + "/" + ApiRoutes.Assets.VERSION_DOWNLOAD.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal)
                .Replace("{versionId:guid}", seeded.PendingVersionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage v1DownloadResponse = await buyer.GetAsync(new Uri(v1Download, UriKind.Relative));
            v1DownloadResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            (await v1DownloadResponse.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo(v1Bytes);
            using HttpResponseMessage v2 = await buyer.GetAsync(new Uri(v2Download, UriKind.Relative));
            v2.StatusCode.Should().Be(HttpStatusCode.OK);
            (await v2.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo(v2Bytes);
            using HttpClient anonymous = fixture.Factory.CreateClient();
            var publicResponses = await ReadRawPublicResponses(anonymous,
                ASSETS_BASE + ApiRoutes.Assets.LIST + "?page=1&pageSize=20&search=" + Uri.EscapeDataString("Pending public title"),
                detailUrl,
                ASSETS_BASE + "/" + ApiRoutes.Assets.VERSIONS.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal));
            publicResponses[0].Should().Contain(seeded.AssetId.ToString());
            publicResponses[1].Should().Contain("Pending public title");
            publicResponses[2].Should().Contain(seeded.PendingVersionId.ToString());
        }
    }

    [Fact]
    public async Task Decision_WhenOlderVersionIsApprovedAfterNewerIsPublic_ShouldRecordHistoricalWithoutMovingPointer()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient olderReviewer, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        (HttpClient newerReviewer, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        (HttpClient buyer, var buyerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        using (olderReviewer)
        using (newerReviewer)
        using (buyer)
        using (HttpClient anonymous = fixture.Factory.CreateClient())
        {
            TwoVersionSeed seed = await SeedTwoReviewableVersionsAsync(ownerName);
            IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
            Guid buyerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, buyerName);
            await AssetVersionsSeed.SeedPurchaseAsync(scopeFactory, buyerId, seed.AssetId, seed.OlderVersionId);
            await SeedEncryptedContentAsync(await AssetVersionsSeed.GetVersionStorageKeyAsync(scopeFactory, seed.OlderVersionId), "older v1 bytes"u8.ToArray());
            await SeedEncryptedContentAsync(await AssetVersionsSeed.GetVersionStorageKeyAsync(scopeFactory, seed.NewerVersionId), "newer v2 bytes"u8.ToArray());

            // The older case stays claimed by one Moderator. The newer case is claimed and approved by another.
            using (HttpResponseMessage olderClaimed = await ModerationDecisionFixture.PostClaimAsync(olderReviewer, seed.OlderSubmissionId, Guid.NewGuid(), 1))
            { olderClaimed.StatusCode.Should().Be(HttpStatusCode.OK, await olderClaimed.Content.ReadAsStringAsync()); }
            using (HttpResponseMessage newerClaimed = await ModerationDecisionFixture.PostClaimAsync(newerReviewer, seed.NewerSubmissionId, Guid.NewGuid(), 1))
            { newerClaimed.StatusCode.Should().Be(HttpStatusCode.OK, await newerClaimed.Content.ReadAsStringAsync()); }
            var newerBasis = await ReadDecisionBasis(seed.NewerSubmissionId, seed.NewerReportId);
            using (HttpResponseMessage newerDecision = await ModerationDecisionFixture.PostDecisionAsync(
                newerReviewer, seed.NewerSubmissionId, ModerationDecisionFixture.DecisionBody(Guid.NewGuid(), newerBasis, "APPROVE")))
            {
                newerDecision.StatusCode.Should().Be(HttpStatusCode.OK, await newerDecision.Content.ReadAsStringAsync());
                using var newerJson = JsonDocument.Parse(await newerDecision.Content.ReadAsStringAsync());
                newerJson.RootElement.GetProperty("promoted").GetBoolean().Should().BeTrue();
            }
            PublicState afterNewer = await ReadPublicStateAsync(seed.AssetId);
            afterNewer.Pointer.Should().NotBeNull();
            ModerationSubmissionState olderStateBefore = await ReadSubmissionStateAsync(seed.OlderSubmissionId);
            olderStateBefore.Should().Be(ModerationSubmissionState.IN_REVIEW, "approving the newer version must not supersede the older case");

            // Late approval of the older version through the same route. Keep the body to replay it later.
            var olderBasis = await ReadDecisionBasis(seed.OlderSubmissionId, seed.OlderReportId);
            var lateBody = ModerationDecisionFixture.DecisionBody(Guid.NewGuid(), olderBasis, "APPROVE");
            Guid lateDecisionId;
            using (HttpResponseMessage late = await ModerationDecisionFixture.PostDecisionAsync(olderReviewer, seed.OlderSubmissionId, lateBody))
            {
                late.StatusCode.Should().Be(HttpStatusCode.OK, await late.Content.ReadAsStringAsync());
                using var lateJson = JsonDocument.Parse(await late.Content.ReadAsStringAsync());
                lateJson.RootElement.GetProperty("promoted").GetBoolean().Should().BeFalse();
                lateJson.RootElement.GetProperty("state").GetString().Should().Be("APPROVED");
                lateJson.RootElement.GetProperty("publicationSnapshotId").GetGuid().Should().NotBe(afterNewer.Pointer!.Value);
                lateDecisionId = lateJson.RootElement.GetProperty("decisionId").GetGuid();
            }

            PublicState afterLate = await ReadPublicStateAsync(seed.AssetId);
            afterLate.Pointer.Should().Be(afterNewer.Pointer, "the older approval must never move the public pointer back");
            afterLate.SearchRevision.Should().Be(afterNewer.SearchRevision);
            afterLate.Snapshots.Should().Be(afterNewer.Snapshots + 1, "the older approval records one historical snapshot");
            afterLate.Decisions.Should().Be(afterNewer.Decisions + 1);
            afterLate.DecisionOutbox.Should().Be(afterNewer.DecisionOutbox + 1);
            (await ReadLatestDecisionPromotionAsync(lateDecisionId)).Should().Be("NOT_PROMOTED");

            // Public surfaces still serve the newer version: title, bytes, default download, and the older version's entitlement.
            var detailUrl = ASSETS_BASE + "/" + ApiRoutes.Assets.ID.Replace("{id:guid}", seed.AssetId.ToString(), StringComparison.Ordinal);
            var detailBody = await (await anonymous.GetAsync(new Uri(detailUrl, UriKind.Relative))).Content.ReadAsStringAsync();
            detailBody.Should().Contain(seed.NewerTitle).And.NotContain(seed.OlderTitle);
            var olderSearch = await ReadRawPublicResponses(anonymous, CatalogSearchUrl(seed.OlderTitle), CatalogSearchUrl(seed.NewerTitle));
            olderSearch[0].Should().NotContain(seed.AssetId.ToString(), "the older approved title is not the public title");
            olderSearch[1].Should().Contain(seed.AssetId.ToString());
            var versionsBody = (await ReadRawPublicResponses(anonymous, ASSETS_BASE + "/" + ApiRoutes.Assets.VERSIONS.Replace("{id:guid}", seed.AssetId.ToString(), StringComparison.Ordinal)))[0];
            versionsBody.Should().Contain(seed.NewerVersionId.ToString());
            using (HttpResponseMessage defaultDownload = await buyer.GetAsync(new Uri(ASSETS_BASE + "/" + ApiRoutes.Assets.DOWNLOAD.Replace("{id:guid}", seed.AssetId.ToString(), StringComparison.Ordinal), UriKind.Relative)))
            {
                defaultDownload.StatusCode.Should().Be(HttpStatusCode.OK);
                (await defaultDownload.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo("newer v2 bytes"u8.ToArray());
            }
            using (HttpResponseMessage olderEntitled = await buyer.GetAsync(new Uri(VersionDownloadUrl(seed.AssetId, seed.OlderVersionId), UriKind.Relative)))
            {
                olderEntitled.StatusCode.Should().Be(HttpStatusCode.OK);
                (await olderEntitled.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo("older v1 bytes"u8.ToArray());
            }

            // Replaying the identical request returns the stored outcome and writes nothing new.
            using (HttpResponseMessage replay = await ModerationDecisionFixture.PostDecisionAsync(olderReviewer, seed.OlderSubmissionId, lateBody))
            {
                replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
                using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
                replayJson.RootElement.GetProperty("replayed").GetBoolean().Should().BeTrue();
                replayJson.RootElement.GetProperty("decisionId").GetGuid().Should().Be(lateDecisionId);
            }
            PublicState afterReplay = await ReadPublicStateAsync(seed.AssetId);
            afterReplay.Should().BeEquivalentTo(afterLate, "a replay must not create a snapshot, decision or outbox row");
        }
    }

    private sealed record PublicState(Guid? Pointer, long SearchRevision, int Snapshots, int Decisions, int DecisionOutbox);

    private async Task<PublicState> ReadPublicStateAsync(Guid assetId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Asset asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);
        var assetIdText = assetId.ToString();
        List<string> decisionPayloads = await db.OutboxMessages.AsNoTracking()
            .Where(o => o.Type == OutboxMessageTypes.MODERATION_DECISION)
            .Select(o => o.Payload)
            .ToListAsync();
        return new PublicState(
            asset.CurrentPublicationSnapshotId,
            asset.SearchRevision,
            await db.PublicationSnapshots.CountAsync(p => p.AssetId == assetId),
            await db.ModerationDecisionRecords.CountAsync(d => d.AssetId == assetId),
            decisionPayloads.Count(p => p.Contains(assetIdText, StringComparison.Ordinal)));
    }

    private async Task<string> ReadLatestDecisionPromotionAsync(Guid decisionId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var decisionText = decisionId.ToString();
        List<string> payloads = await db.OutboxMessages.AsNoTracking()
            .Where(o => o.Type == OutboxMessageTypes.MODERATION_DECISION)
            .Select(o => o.Payload)
            .ToListAsync();
        var payload = payloads.Single(p => p.Contains(decisionText, StringComparison.Ordinal));
        using var json = JsonDocument.Parse(payload);
        return json.RootElement.GetProperty("promotion").GetString()!;
    }

    private async Task<ModerationSubmissionState> ReadSubmissionStateAsync(Guid submissionId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.ModerationSubmissions.AsNoTracking().Where(s => s.Id == submissionId).Select(s => s.State).SingleAsync();
    }

    private static string CatalogSearchUrl(string search) =>
        ASSETS_BASE + ApiRoutes.Assets.LIST + "?page=1&pageSize=20&search=" + Uri.EscapeDataString(search);

    private static string VersionDownloadUrl(Guid assetId, Guid versionId) =>
        ASSETS_BASE + "/" + ApiRoutes.Assets.VERSION_DOWNLOAD.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal)
            .Replace("{versionId:guid}", versionId.ToString(), StringComparison.Ordinal);

    private sealed record TwoVersionSeed(
        Guid AssetId,
        Guid OlderVersionId,
        Guid NewerVersionId,
        Guid OlderSubmissionId,
        Guid NewerSubmissionId,
        Guid OlderReportId,
        Guid NewerReportId,
        string OlderTitle,
        string NewerTitle);

    /// <summary>Two READY versions with no publication yet; each has its own reviewable case and workspace.</summary>
    private async Task<TwoVersionSeed> SeedTwoReviewableVersionsAsync(string ownerUsername)
    {
        var token = Guid.NewGuid().ToString("N");
        var olderTitle = "Earlier release " + Guid.NewGuid().ToString("N");
        var newerTitle = "Latest revision " + Guid.NewGuid().ToString("N");
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User owner = await db.Users.SingleAsync(u => u.Username == ownerUsername);
        var category = new Category { Id = Guid.NewGuid(), Name = "Late approval", Slug = "late-approval-" + token, CreatedAt = DateTimeOffset.UtcNow };
        var asset = new Asset { Id = Guid.NewGuid(), AuthorId = owner.Id, CategoryId = category.Id, Title = "Draft " + token, Description = "", Price = 1m, CreatedAt = DateTimeOffset.UtcNow };
        AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        AssetVersion older = NewVersion(asset.Id, 1, new string('c', 64), license, AssetVersionProcessingStatus.READY);
        AssetVersion newer = NewVersion(asset.Id, 2, new string('d', 64), license, AssetVersionProcessingStatus.READY);
        db.Categories.Add(category);
        db.Assets.Add(asset);
        db.AssetVersions.Add(older);
        db.AssetVersions.Add(newer);
        await db.SaveChangesAsync();
        ModerationSubmission olderSubmission = ModerationDecisionFixture.AddReviewableSubmission(db, asset, older, owner);
        ModerationSubmission newerSubmission = ModerationDecisionFixture.AddReviewableSubmission(db, asset, newer, owner);
        await db.SaveChangesAsync();
        Guid olderReportId = await ModerationDecisionFixture.PrepareDecisionReadySubmission(db, olderSubmission, olderTitle, "Older public description");
        Guid newerReportId = await ModerationDecisionFixture.PrepareDecisionReadySubmission(db, newerSubmission, newerTitle, "Newer public description");
        return new TwoVersionSeed(asset.Id, older.Id, newer.Id, olderSubmission.Id, newerSubmission.Id, olderReportId, newerReportId, olderTitle, newerTitle);
    }

    private static async Task<string[]> ReadRawPublicResponses(HttpClient client, params string[] paths)
    {
        var bodies = new string[paths.Length];
        for (var index = 0; index < paths.Length; index++)
        {
            using HttpResponseMessage response = await client.GetAsync(new Uri(paths[index], UriKind.Relative));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            bodies[index] = await response.Content.ReadAsStringAsync();
        }
        return bodies;
    }

    private Task<object> ReadDecisionBasis(Guid submissionId, Guid reportId) =>
        ModerationDecisionFixture.ReadDecisionBasis(fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), submissionId, reportId);

    [Fact]
    public async Task ApprovedNewerVersion_WhenBuyerPurchasedEarlierVersion_ShouldDownloadNewerBytes()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient buyer, var buyerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        using (buyer)
        {
            Seeded seeded = await SeedApprovedWithPendingVersion(ownerName, ModerationSubmissionState.SUBMITTED, null);
            IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
            Guid buyerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, buyerName);
            await AssetVersionsSeed.SeedPurchaseAsync(scopeFactory, buyerId, seeded.AssetId, seeded.ApprovedVersionId);
            await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
            {
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                ModerationSubmission pending = await db.ModerationSubmissions.SingleAsync(s => s.AssetVersionId == seeded.PendingVersionId);
                AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.SingleAsync(w => w.Id == pending.WorkspaceId);
                db.ModerationSubmissions.Remove(pending);
                db.AssetDraftWorkspaces.Remove(workspace);
                await db.SaveChangesAsync();
                Asset asset = await db.Assets.SingleAsync(a => a.Id == seeded.AssetId);
                AssetVersion version = await db.AssetVersions.SingleAsync(v => v.Id == seeded.PendingVersionId);
                User author = await db.Users.SingleAsync(u => u.Id == asset.AuthorId);
                Category category = await db.Categories.SingleAsync(c => c.Id == asset.CategoryId);
                await TrustedPublicationFixture.AttachTrustedApprovedPublication(db, asset, version, author, category);
            }
            var plaintext = "approved v2 entitled bytes"u8.ToArray();
            await SeedEncryptedContentAsync(await AssetVersionsSeed.GetVersionStorageKeyAsync(scopeFactory, seeded.PendingVersionId), plaintext);
            var path = ASSETS_BASE + "/" + ApiRoutes.Assets.VERSION_DOWNLOAD.Replace("{id:guid}", seeded.AssetId.ToString(), StringComparison.Ordinal)
                .Replace("{versionId:guid}", seeded.PendingVersionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage response = await buyer.GetAsync(new Uri(path, UriKind.Relative));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsByteArrayAsync()).Should().BeEquivalentTo(plaintext);
        }
    }

    private async Task SeedEncryptedContentAsync(string storageKey, byte[] plaintext)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IEncryptionService encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        await using var plain = new MemoryStream(plaintext, writable: false);
        await using var cipher = new MemoryStream();
        await encryptionService.Encrypt(plain, cipher, CancellationToken.None);
        fixture.Factory.AssetStorage.Seed(storageKey, cipher.ToArray());
    }
}
