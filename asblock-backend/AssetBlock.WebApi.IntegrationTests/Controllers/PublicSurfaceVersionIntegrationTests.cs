using System.Net;
using AssetBlock.Domain.Abstractions.Services;
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
public sealed class PublicSurfaceVersionIntegrationTests(IntegrationTestFixture fixture)
{
    private const string PENDING_TITLE = "Pending draft title sentinel";
    private const string ASSETS_BASE = "/" + ApiRoutes.Assets.BASE;

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
            (HttpClient stranger, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
            using (buyer)
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
            }
            await using AsyncServiceScope verify = fixture.Factory.Services.CreateAsyncScope();
            ApplicationDbContext db = verify.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            (await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.AssetVersionId == seeded.PendingVersionId)).State
                .Should().Be(pendingState);
            (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == seeded.AssetId)).CurrentPublicationSnapshotId.Should().NotBeNull();
        }
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
