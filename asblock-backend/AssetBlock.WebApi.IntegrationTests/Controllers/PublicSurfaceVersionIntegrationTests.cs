using System.Net;
using System.Net.Http.Json;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Payments;
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
                reportId = await PrepareDecisionReadySubmission(db, submission);
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
                reportId = await PrepareDecisionReadySubmission(db, submission);
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

    private static async Task<Guid> PrepareDecisionReadySubmission(ApplicationDbContext db, ModerationSubmission submission)
    {
        AssetVersion version = await db.AssetVersions.SingleAsync(v => v.Id == submission.AssetVersionId);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.SingleAsync(w => w.Id == submission.WorkspaceId);
        Asset asset = await db.Assets.SingleAsync(a => a.Id == submission.AssetId);
        Category category = await db.Categories.SingleAsync(c => c.Id == asset.CategoryId);
        workspace.SourceDeclarationHeadRevision = 1;
        workspace.MaterialMetadataHeadRevision = 1;
        var declarationId = Guid.NewGuid();
        var materialId = Guid.NewGuid();
        const string declaration = """{"redistributionAcknowledged":true,"ownContributionSummary":"Original implementation.","components":[]}""";
        var material = System.Text.Json.JsonSerializer.Serialize(new { title = "Pending public title", description = (string?)null, categoryId = category.Id, tags = Array.Empty<string>() });
        var declarationDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(declaration)));
        var materialDigest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(material)));
        db.AssetSourceDeclarationRevisions.Add(new AssetSourceDeclarationRevision
        {
            Id = declarationId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            Revision = 1,
            AuthorUserId = submission.OwnerUserId,
            ContentDigest = declarationDigest,
            SchemaVersion = 1,
            PayloadJson = declaration,
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.AssetMaterialMetadataRevisions.Add(new AssetMaterialMetadataRevision
        {
            Id = materialId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            Revision = 1,
            AuthorUserId = submission.OwnerUserId,
            ContentDigest = materialDigest,
            SchemaVersion = 1,
            PayloadJson = material,
            CreatedAt = DateTimeOffset.UtcNow
        });
        AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.Chain chain =
            AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.BuildChain() with { PolicyDigest = ReviewAnalysisPolicy.DIGEST };
        CodeAnalysisPolicyVersion? policy = await db.CodeAnalysisPolicyVersions.AsNoTracking().SingleOrDefaultAsync(p => p.PolicyKey == ReviewAnalysisPolicy.KEY && p.PolicyDigest == ReviewAnalysisPolicy.DIGEST);
        if (policy is null)
        { db.CodeAnalysisPolicyVersions.Add(AssetBlock.Infrastructure.IntegrationTests.Support.AdmittedReportTestEvidence.Policy(chain)); }
        else
        { chain = chain with { PolicyVersionId = policy.Id }; }
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
        admission.AdmittedBy = "assetblock-dotnet-finalizer";
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
        db.Entry(submission).Property(s => s.CodeAnalysisReportHeaderId).CurrentValue = report.Id;
        db.Entry(submission).Property(s => s.SellerEvidenceDigest).CurrentValue = AssetBlock.Infrastructure.Persistence.ProductDigests.EmptyProjectionDigest(nameof(AssetSellerEvidenceRevision));
        await db.SaveChangesAsync();
        await db.AssetVersions.Where(candidate => candidate.Id == version.Id)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.SelectedCodeAnalysisReportId, report.Id));
        return report.Id;
    }

    private async Task<object> ReadDecisionBasis(Guid submissionId, Guid reportId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var basis = await db.ModerationSubmissions.AsNoTracking().Where(s => s.Id == submissionId)
            .Select(s => new { s.AssetVersionId, s.ContentSha256, s.DeclarationRevision, s.MaterialMetadataRevision, s.PolicyVersion, s.SellerEvidenceRevision, s.CaseRevision })
            .SingleAsync();
        return new
        {
            versionId = basis.AssetVersionId,
            contentSha256 = basis.ContentSha256,
            analysisReportId = reportId,
            declarationRevision = basis.DeclarationRevision,
            materialMetadataRevision = basis.MaterialMetadataRevision,
            policyVersion = basis.PolicyVersion,
            sellerEvidenceRevision = basis.SellerEvidenceRevision,
            caseRevision = basis.CaseRevision
        };
    }

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
