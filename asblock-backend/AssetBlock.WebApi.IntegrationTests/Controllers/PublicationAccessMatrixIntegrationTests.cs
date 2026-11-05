using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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

/// <summary>
/// Publication and access matrix. One asset per latest-version state: v1 is approved and public,
/// v2 is the newest version and is not public. Actors and routes are enumerated by authorization
/// class; each cell asserts the status and the body rule for that class.
/// Current behavior asserted as-is: the asset author (and the same account once it also holds the
/// Moderator role) may download the bytes of any READY version, including the pending v2, and sees
/// every version in the list.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class PublicationAccessMatrixIntegrationTests(IntegrationTestFixture fixture)
{
    private const string ASSETS_BASE = "/" + ApiRoutes.Assets.BASE;

    private const string NEVER_SUBMITTED = "READY_NEVER_SUBMITTED";

    private enum ActorKind
    {
        Anonymous,
        Stranger,
        Buyer,
        Author,
        AuthorModerator,
        OtherModerator,
        Admin
    }

    private enum AccessRoute
    {
        AssetDownload,
        ApprovedVersionDownload,
        PendingVersionDownload,
        VersionList,
        Detail,
        CatalogApprovedTitle,
        CatalogPendingTitle,
        Checkout
    }

    private sealed record MatrixAsset(
        Guid AssetId,
        Guid ApprovedVersionId,
        Guid PendingVersionId,
        Guid? PublicationSnapshotId,
        Guid? SubmissionId,
        Guid? ReportId,
        string ApprovedTitle,
        string ApprovedDescription,
        string ApprovedContent,
        string PendingTitle,
        string PendingDescription,
        string PendingContent,
        string StateLabel);

    private sealed record MatrixResponse(HttpStatusCode Status, byte[] Body);

    [Theory]
    [InlineData(ModerationSubmissionState.SUBMITTED)]
    [InlineData(ModerationSubmissionState.IN_REVIEW)]
    [InlineData(ModerationSubmissionState.CHANGES_REQUESTED)]
    [InlineData(ModerationSubmissionState.REJECTED)]
    [InlineData(ModerationSubmissionState.WITHDRAWN)]
    [InlineData(null)]
    public async Task AccessMatrix_WhenLatestVersionIsNotPublic_ShouldEnforceExpectedStatusPerActorAndRoute(ModerationSubmissionState? latestState)
    {
        (HttpClient author, var authorName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient stranger, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient buyer, var buyerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient otherModerator, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        (HttpClient admin, _) = await IntegrationTestAuth.RegisterAdminAndAuthenticateAsync(fixture.Factory);
        using HttpClient anonymous = fixture.Factory.CreateClient();
        HttpClient? authorModerator = null;
        try
        {
            using (author)
            using (stranger)
            using (buyer)
            using (otherModerator)
            using (admin)
            {
                MatrixAsset asset = await SeedAssetAsync(authorName, latestState);
                await DriveReviewStateAsync(asset, otherModerator);

                IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
                Guid buyerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, buyerName);
                await AssetVersionsSeed.SeedPurchaseAsync(scopeFactory, buyerId, asset.AssetId, asset.ApprovedVersionId);
                await SeedEncryptedContentAsync(await AssetVersionsSeed.GetVersionStorageKeyAsync(scopeFactory, asset.ApprovedVersionId), asset.ApprovedContent);
                await SeedEncryptedContentAsync(await AssetVersionsSeed.GetVersionStorageKeyAsync(scopeFactory, asset.PendingVersionId), asset.PendingContent);

                var checkoutIntentIds = new List<Guid>();
                await AssertActorRoutesAsync(ActorKind.Anonymous, anonymous, asset, checkoutIntentIds);
                await AssertActorRoutesAsync(ActorKind.Stranger, stranger, asset, checkoutIntentIds);
                await AssertActorRoutesAsync(ActorKind.Buyer, buyer, asset, checkoutIntentIds);
                await AssertActorRoutesAsync(ActorKind.Author, author, asset, checkoutIntentIds);
                await AssertActorRoutesAsync(ActorKind.OtherModerator, otherModerator, asset, checkoutIntentIds);
                await AssertActorRoutesAsync(ActorKind.Admin, admin, asset, checkoutIntentIds);

                // The same account as the author, now also holding the Moderator role (fresh token after promotion).
                authorModerator = await IntegrationTestAuth.PromoteToModeratorAsync(fixture.Factory, authorName);
                await AssertActorRoutesAsync(ActorKind.AuthorModerator, authorModerator, asset, checkoutIntentIds);

                // Only stranger, other moderator and admin may check out. Every denial must leave no rows.
                checkoutIntentIds.Should().HaveCount(3, "exactly stranger, other moderator and admin may check out");
                await AssertNoDenialSideEffectsAsync(asset, checkoutIntentIds.Count);
            }
        }
        finally
        {
            authorModerator?.Dispose();
        }
    }

    private async Task AssertActorRoutesAsync(
        ActorKind actor,
        HttpClient client,
        MatrixAsset asset,
        List<Guid> checkoutIntentIds)
    {
        foreach (AccessRoute route in Enum.GetValues<AccessRoute>())
        {
            var cell = Cell(actor, asset.StateLabel, route);
            MatrixResponse response = await SendAsync(client, route, asset);

            response.Status.Should().Be(ExpectedStatus(actor, route), cell + " status");
            AssertBody(actor, route, response.Status, response.Body, asset, cell);

            if (route == AccessRoute.Checkout && response.Status == HttpStatusCode.OK)
            {
                CreateCheckoutSessionResponse? checkout = JsonSerializer.Deserialize<CreateCheckoutSessionResponse>(response.Body, IntegrationTestAuth.JsonOptions);
                checkout.Should().NotBeNull(cell + " checkout body");
                checkoutIntentIds.Add(checkout!.CheckoutIntentId);
                await AssertCheckoutItemIsApprovedVersionAsync(checkout.CheckoutIntentId, asset, cell);
            }
        }
    }

    /// <summary>
    /// Expected status per authorization class. Sources: DownloadService.AuthorizeDownload (author and
    /// purchase checks), AssetStore.ListVersions (author sees all versions), and the checkout handler
    /// (own-asset 403 is checked before the existing-purchase 409).
    /// </summary>
    private static HttpStatusCode ExpectedStatus(ActorKind actor, AccessRoute route) => route switch
    {
        AccessRoute.Checkout => actor switch
        {
            ActorKind.Anonymous => HttpStatusCode.Unauthorized,
            ActorKind.Buyer => HttpStatusCode.Conflict,
            ActorKind.Author or ActorKind.AuthorModerator => HttpStatusCode.Forbidden,
            _ => HttpStatusCode.OK
        },
        AccessRoute.VersionList or AccessRoute.Detail or AccessRoute.CatalogApprovedTitle or AccessRoute.CatalogPendingTitle
            => HttpStatusCode.OK,
        AccessRoute.PendingVersionDownload => actor switch
        {
            ActorKind.Anonymous => HttpStatusCode.Unauthorized,
            ActorKind.Buyer => HttpStatusCode.NotFound,
            ActorKind.Author or ActorKind.AuthorModerator => HttpStatusCode.OK,
            _ => HttpStatusCode.Forbidden
        },
        _ => actor switch
        {
            ActorKind.Anonymous => HttpStatusCode.Unauthorized,
            ActorKind.Stranger or ActorKind.OtherModerator or ActorKind.Admin => HttpStatusCode.Forbidden,
            _ => HttpStatusCode.OK
        }
    };

    private static void AssertBody(ActorKind actor, AccessRoute route, HttpStatusCode status, byte[] body, MatrixAsset asset, string cell)
    {
        var text = Encoding.UTF8.GetString(body);
        var isOk = status == HttpStatusCode.OK;

        switch (route)
        {
            case AccessRoute.AssetDownload:
            case AccessRoute.ApprovedVersionDownload:
                if (isOk)
                {
                    body.Should().BeEquivalentTo(Encoding.UTF8.GetBytes(asset.ApprovedContent), cell + " approved bytes");
                }
                else
                {
                    text.Should().NotContain(asset.PendingContent, cell + " denial must not carry pending bytes");
                }
                break;

            case AccessRoute.PendingVersionDownload:
                if (isOk)
                {
                    body.Should().BeEquivalentTo(Encoding.UTF8.GetBytes(asset.PendingContent), cell + " pending bytes");
                }
                else
                {
                    text.Should().NotContain(asset.PendingContent, cell + " denial must not carry pending bytes");
                }
                break;

            case AccessRoute.VersionList:
                text.Should().Contain(asset.ApprovedVersionId.ToString(), cell + " approved version listed");
                if (actor is ActorKind.Author or ActorKind.AuthorModerator)
                {
                    text.Should().Contain(asset.PendingVersionId.ToString(), cell + " author sees own pending version");
                }
                else
                {
                    text.Should().NotContain(asset.PendingVersionId.ToString(), cell + " pending version must stay hidden");
                }
                AssertNoPendingMetadata(text, asset, cell);
                break;

            case AccessRoute.Detail:
                text.Should().Contain(asset.ApprovedTitle, cell + " approved title");
                text.Should().Contain(asset.ApprovedDescription, cell + " approved description");
                text.Should().NotContain(asset.PendingVersionId.ToString(), cell + " pending version id");
                AssertNoPendingMetadata(text, asset, cell);
                break;

            case AccessRoute.CatalogApprovedTitle:
                text.Should().Contain(asset.AssetId.ToString(), cell + " listed by approved title");
                text.Should().Contain(asset.ApprovedTitle, cell + " approved title");
                text.Should().NotContain(asset.PendingVersionId.ToString(), cell + " pending version id");
                AssertNoPendingMetadata(text, asset, cell);
                break;

            case AccessRoute.CatalogPendingTitle:
                text.Should().NotContain(asset.AssetId.ToString(), cell + " pending title must not match the asset");
                AssertNoPendingMetadata(text, asset, cell);
                break;

            case AccessRoute.Checkout:
                if (!isOk)
                {
                    text.Should().NotContain(asset.PendingVersionId.ToString(), cell + " denial must not name pending version");
                }
                break;
        }
    }

    private static void AssertNoPendingMetadata(string text, MatrixAsset asset, string cell)
    {
        text.Should().NotContain(asset.PendingTitle, cell + " pending title");
        text.Should().NotContain(asset.PendingDescription, cell + " pending description");
    }

    private async Task AssertCheckoutItemIsApprovedVersionAsync(Guid checkoutIntentId, MatrixAsset asset, string cell)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CheckoutIntentItems.AsNoTracking()
            .Where(i => i.CheckoutIntentId == checkoutIntentId)
            .Select(i => i.AssetVersionId)
            .SingleAsync())
            .Should().Be(asset.ApprovedVersionId, cell + " checkout must sell the approved version only");
    }

    private static string Cell(ActorKind actor, string state, AccessRoute route) =>
        $"actor {actor}, latest version {state}, route {route}";

    private static async Task<MatrixResponse> SendAsync(HttpClient client, AccessRoute route, MatrixAsset asset)
    {
        if (route == AccessRoute.Checkout)
        {
            using HttpResponseMessage post = await client.PostAsJsonAsync(
                new Uri(IntegrationTestRoutes.PAYMENTS_BASE + "/" + ApiRoutes.Payments.CHECKOUT, UriKind.Relative),
                new CreateCheckoutRequest(asset.AssetId));
            return new MatrixResponse(post.StatusCode, await post.Content.ReadAsByteArrayAsync());
        }

        var url = route switch
        {
            AccessRoute.AssetDownload => ASSETS_BASE + "/" + ApiRoutes.Assets.DOWNLOAD.Replace("{id:guid}", asset.AssetId.ToString(), StringComparison.Ordinal),
            AccessRoute.ApprovedVersionDownload => VersionDownloadUrl(asset.AssetId, asset.ApprovedVersionId),
            AccessRoute.PendingVersionDownload => VersionDownloadUrl(asset.AssetId, asset.PendingVersionId),
            AccessRoute.VersionList => ASSETS_BASE + "/" + ApiRoutes.Assets.VERSIONS.Replace("{id:guid}", asset.AssetId.ToString(), StringComparison.Ordinal),
            AccessRoute.Detail => ASSETS_BASE + "/" + ApiRoutes.Assets.ID.Replace("{id:guid}", asset.AssetId.ToString(), StringComparison.Ordinal),
            AccessRoute.CatalogApprovedTitle => CatalogUrl(asset.ApprovedTitle),
            AccessRoute.CatalogPendingTitle => CatalogUrl(asset.PendingTitle),
            _ => throw new ArgumentOutOfRangeException(nameof(route), route, null)
        };

        using HttpResponseMessage response = await client.GetAsync(new Uri(url, UriKind.Relative));
        return new MatrixResponse(response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }

    private static string VersionDownloadUrl(Guid assetId, Guid versionId) =>
        ASSETS_BASE + "/" + ApiRoutes.Assets.VERSION_DOWNLOAD
            .Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal)
            .Replace("{versionId:guid}", versionId.ToString(), StringComparison.Ordinal);

    private static string CatalogUrl(string search) =>
        ASSETS_BASE + ApiRoutes.Assets.LIST + "?page=1&pageSize=20&search=" + Uri.EscapeDataString(search);

    private async Task AssertNoDenialSideEffectsAsync(MatrixAsset asset, int successfulCheckouts)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var cell = $"latest version {asset.StateLabel}";

        // One seeded purchase plus one intent per successful checkout; no denied checkout may add a row.
        (await db.CheckoutIntents.AsNoTracking().CountAsync(i => i.AssetId == asset.AssetId))
            .Should().Be(1 + successfulCheckouts, cell + " checkout intents");
        (await db.Purchases.AsNoTracking().CountAsync(p => p.AssetId == asset.AssetId))
            .Should().Be(1, cell + " purchases");
        (await db.Orders.AsNoTracking().CountAsync(o => o.AssetId == asset.AssetId))
            .Should().Be(1, cell + " orders");

        Asset row = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.AssetId);
        row.CurrentPublicationSnapshotId.Should().Be(asset.PublicationSnapshotId, cell + " publication pointer");

        List<ModerationSubmissionState> submissionStates = await db.ModerationSubmissions.AsNoTracking()
            .Where(s => s.AssetVersionId == asset.PendingVersionId)
            .Select(s => s.State)
            .ToListAsync();
        if (asset.StateLabel == NEVER_SUBMITTED)
        {
            submissionStates.Should().BeEmpty(cell + " must not gain a submission");
        }
        else
        {
            ModerationSubmissionState expectedState = Enum.Parse<ModerationSubmissionState>(asset.StateLabel);
            submissionStates.Should().Equal(new[] { expectedState }, cell + " submission state");
        }
    }

    /// <summary>
    /// Moves a not-public v2 through the real review routes: IN_REVIEW by claim, CHANGES_REQUESTED and
    /// REJECTED by claim plus decision. SUBMITTED, WITHDRAWN and never-submitted rows are seeded directly.
    /// </summary>
    private async Task DriveReviewStateAsync(MatrixAsset asset, HttpClient reviewer)
    {
        if (asset.StateLabel is not ("IN_REVIEW" or "CHANGES_REQUESTED" or "REJECTED") || asset.SubmissionId is not { } submissionId)
        {
            return;
        }

        var claimPath = "/" + ApiRoutes.Moderation.CLAIM.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
        using HttpResponseMessage claimed = await reviewer.PostAsJsonAsync(claimPath, new { operationId = Guid.NewGuid(), expectedCaseRevision = 1 });
        claimed.StatusCode.Should().Be(HttpStatusCode.OK, asset.StateLabel + " claim: " + await claimed.Content.ReadAsStringAsync());
        if (asset.StateLabel == "IN_REVIEW")
        {
            return;
        }

        var outcome = asset.StateLabel == "REJECTED" ? "REJECT" : "REQUEST_CHANGES";
        var basis = await ReadDecisionBasisAsync(asset, submissionId);
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
        decision.StatusCode.Should().Be(HttpStatusCode.OK, asset.StateLabel + " decision: " + await decision.Content.ReadAsStringAsync());
    }

    private async Task<object> ReadDecisionBasisAsync(MatrixAsset asset, Guid submissionId) =>
        await ModerationDecisionFixture.ReadDecisionBasis(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            submissionId,
            asset.ReportId!.Value);

    private async Task<MatrixAsset> SeedAssetAsync(string ownerUsername, ModerationSubmissionState? latestState)
    {
        var token = Guid.NewGuid().ToString("N");
        var approvedTitle = "Approved " + token;
        var approvedDescription = "Approved description " + token;
        // A distinct token: shared text would let trigram search match the pending title through the approved one.
        var pendingToken = Guid.NewGuid().ToString("N");
        var pendingTitle = "Pending " + pendingToken;
        var pendingDescription = "Pending description " + pendingToken;
        var approvedContent = "approved v1 bytes " + token;
        var pendingContent = "pending v2 bytes " + token;
        var stateLabel = latestState?.ToString() ?? NEVER_SUBMITTED;

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User owner = await db.Users.SingleAsync(u => u.Username == ownerUsername);
        var category = new Category { Id = Guid.NewGuid(), Name = "Access matrix", Slug = "access-matrix-" + token, CreatedAt = DateTimeOffset.UtcNow };
        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            AuthorId = owner.Id,
            CategoryId = category.Id,
            Title = approvedTitle,
            Description = approvedDescription,
            Price = 1m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        AssetVersion approved = NewVersion(asset.Id, 1, new string('a', 64), license);
        AssetVersion pending = NewVersion(asset.Id, 2, new string('b', 64), license);
        db.Categories.Add(category);
        db.Assets.Add(asset);
        db.AssetVersions.Add(approved);
        db.AssetVersions.Add(pending);
        await db.SaveChangesAsync();
        await TrustedPublicationFixture.AttachTrustedApprovedPublication(db, asset, approved, owner, category, approvedTitle);

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var workspace = new AssetDraftWorkspace
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = pending.Id,
            WorkspaceVersionScopeKey = pending.Id,
            ScopeId = Guid.NewGuid(),
            CreatedAt = now,
            MaterialMetadataHeadRevision = 1
        };
        db.AssetDraftWorkspaces.Add(workspace);

        Guid? submissionId = null;
        Guid? reportId = null;
        if (latestState is { } state)
        {
            var submission = new ModerationSubmission
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
                State = state == ModerationSubmissionState.WITHDRAWN ? ModerationSubmissionState.WITHDRAWN : ModerationSubmissionState.SUBMITTED,
                CaseRevision = 1,
                CreatedAt = now
            };
            db.ModerationSubmissions.Add(submission);
            await db.SaveChangesAsync();
            submissionId = submission.Id;

            // A decision-capable report for every submission a Moderator can reach through HTTP. It also writes
            // the draft material revision carrying the pending title and description.
            if (state != ModerationSubmissionState.WITHDRAWN)
            {
                reportId = await ModerationDecisionFixture.PrepareDecisionReadySubmission(db, submission, pendingTitle, pendingDescription);
            }
        }

        if (reportId is null)
        {
            // Never-submitted and withdrawn drafts have no decision report; their draft material is written directly.
            var material = JsonSerializer.Serialize(new { title = pendingTitle, description = pendingDescription, categoryId = category.Id, tags = Array.Empty<string>() });
            db.AssetMaterialMetadataRevisions.Add(new AssetMaterialMetadataRevision
            {
                Id = Guid.NewGuid(),
                AssetId = asset.Id,
                AssetVersionId = pending.Id,
                WorkspaceId = workspace.Id,
                WorkspaceVersionScopeKey = pending.Id,
                Revision = 1,
                AuthorUserId = owner.Id,
                ContentDigest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))),
                SchemaVersion = 1,
                PayloadJson = material,
                CreatedAt = now
            });
        }

        await db.SaveChangesAsync();

        return new MatrixAsset(
            asset.Id,
            approved.Id,
            pending.Id,
            asset.CurrentPublicationSnapshotId,
            submissionId,
            reportId,
            approvedTitle,
            approvedDescription,
            approvedContent,
            pendingTitle,
            pendingDescription,
            pendingContent,
            stateLabel);
    }

    private async Task SeedEncryptedContentAsync(string storageKey, string plaintext)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        IEncryptionService encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();
        await using var plain = new MemoryStream(Encoding.UTF8.GetBytes(plaintext), writable: false);
        await using var cipher = new MemoryStream();
        await encryptionService.Encrypt(plain, cipher, CancellationToken.None);
        fixture.Factory.AssetStorage.Seed(storageKey, cipher.ToArray());
    }

    private static AssetVersion NewVersion(Guid assetId, int number, string sha, AssetLicenseTemplate license) => new()
    {
        Id = Guid.NewGuid(),
        AssetId = assetId,
        VersionNumber = number,
        IsCurrent = number == 1,
        StorageKey = "access-matrix/" + Guid.NewGuid().ToString("N") + "/source.bin",
        FileName = "source.zip",
        ContentLength = 1,
        ContentSha256 = sha,
        ReleaseNotes = "Fixture",
        LicenseCode = license.Code,
        LicenseTemplateVersion = license.TemplateVersion,
        LicenseDisplayName = license.DisplayName,
        LicenseTerms = license.TermsPlainText,
        ProcessingStatus = AssetVersionProcessingStatus.READY,
        ProcessingUpdatedAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow
    };
}
