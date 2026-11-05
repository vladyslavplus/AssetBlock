using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// An asset whose only version is READY and submitted, with no approved decision and no current publication
/// snapshot. The routes read are catalog search, detail, version list, asset download, version download and
/// checkout. An approved control asset is read on the detail and version-list routes as well, so each negative
/// result on the target is shown to come from a route that serves public assets.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class NeverApprovedFirstVersionIntegrationTests(IntegrationTestFixture fixture)
{
    private const string ASSETS_BASE = "/" + ApiRoutes.Assets.BASE;

    [Fact]
    public async Task NeverApprovedFirstVersion_WhenReadyOnly_ShouldNotBeExposedOnTheReadRoutes()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient stranger, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient buyer, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using HttpClient anonymous = fixture.Factory.CreateClient();
        using (owner)
        using (stranger)
        using (buyer)
        {
            var token = "NeverApproved" + Guid.NewGuid().ToString("N");
            var controlToken = "ApprovedControl" + Guid.NewGuid().ToString("N");
            (Guid assetId, Guid versionId) = await SeedReadyOnlyFirstVersionAsync(ownerName, token + " first version");
            (Guid controlId, Guid controlVersionId) = await SeedApprovedControlAsync(ownerName, controlToken + " approved");

            // Positive controls: the approved asset is listed by search, its detail is served, and its version list
            // shows its approved version, all to an anonymous caller.
            using (HttpResponseMessage controlSearch = await anonymous.GetAsync(new Uri(CatalogUrl(controlToken), UriKind.Relative)))
            {
                controlSearch.StatusCode.Should().Be(HttpStatusCode.OK, "control catalog search");
                (await ListedIdsAsync(controlSearch)).Should().Contain(controlId);
            }

            using (HttpResponseMessage controlDetail = await anonymous.GetAsync(new Uri(AssetPath(controlId), UriKind.Relative)))
            {
                controlDetail.StatusCode.Should().Be(HttpStatusCode.OK, "control detail");
            }

            using (HttpResponseMessage controlVersions = await anonymous.GetAsync(new Uri(VersionsPath(controlId), UriKind.Relative)))
            {
                controlVersions.StatusCode.Should().Be(HttpStatusCode.OK, "control version list");
                using var controlBody = JsonDocument.Parse(await controlVersions.Content.ReadAsStringAsync());
                controlBody.RootElement.EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).Should().Contain(controlVersionId);
            }

            // Non-purchaser downloads return 403 because of the purchase check (DownloadService.cs:31-37), which runs
            // before any publication check. The 404 detail and checkout, and the empty version list, carry the
            // publication gate. Anonymous callers get 401 from [Authorize] on the download routes.
            Caller[] callers =
            [
                new Caller("anonymous", anonymous, HttpStatusCode.Unauthorized, HttpStatusCode.Unauthorized),
                new Caller("other seller", stranger, HttpStatusCode.Forbidden, HttpStatusCode.NotFound),
                new Caller("buyer without purchase", buyer, HttpStatusCode.Forbidden, HttpStatusCode.NotFound),
            ];
            foreach (Caller caller in callers)
            {
                var actor = caller.Actor;
                HttpClient client = caller.Client;

                using (HttpResponseMessage search = await client.GetAsync(new Uri(CatalogUrl(token), UriKind.Relative)))
                {
                    search.StatusCode.Should().Be(HttpStatusCode.OK, actor + " catalog search");
                    (await ListedIdsAsync(search)).Should().NotContain(assetId, actor + " must not see the first version in the catalog");
                }

                using (HttpResponseMessage detail = await client.GetAsync(new Uri(AssetPath(assetId), UriKind.Relative)))
                {
                    detail.StatusCode.Should().Be(HttpStatusCode.NotFound, actor + " detail");
                }

                using (HttpResponseMessage versions = await client.GetAsync(new Uri(VersionsPath(assetId), UriKind.Relative)))
                {
                    versions.StatusCode.Should().Be(HttpStatusCode.OK, actor + " version list");
                    using var body = JsonDocument.Parse(await versions.Content.ReadAsStringAsync());
                    body.RootElement.ValueKind.Should().Be(JsonValueKind.Array, actor + " version list body");
                    body.RootElement.GetArrayLength().Should().Be(0, actor + " must not see the READY first version in the version list");
                }

                using (HttpResponseMessage download = await client.GetAsync(new Uri(AssetDownloadPath(assetId), UriKind.Relative)))
                {
                    download.StatusCode.Should().Be(caller.DownloadStatus, actor + " asset download");
                }

                using (HttpResponseMessage versionDownload = await client.GetAsync(new Uri(VersionDownloadPath(assetId, versionId), UriKind.Relative)))
                {
                    versionDownload.StatusCode.Should().Be(caller.DownloadStatus, actor + " version download");
                }

                using (HttpResponseMessage checkout = await client.PostAsJsonAsync(
                    new Uri(IntegrationTestRoutes.PAYMENTS_BASE + "/" + ApiRoutes.Payments.CHECKOUT, UriKind.Relative),
                    new CreateCheckoutRequest(assetId)))
                {
                    checkout.StatusCode.Should().Be(caller.CheckoutStatus, actor + " checkout");
                }
            }

            await AssertNoCheckoutIntentAsync(assetId);

            // The author: the public gate also hides the version from the owner's detail and checkout. The owner's
            // version list shows every version, so the READY first version is listed there (current behaviour).
            using (HttpResponseMessage ownerDetail = await owner.GetAsync(new Uri(AssetPath(assetId), UriKind.Relative)))
            {
                ownerDetail.StatusCode.Should().Be(HttpStatusCode.NotFound, "owner detail follows the same public gate");
            }

            using (HttpResponseMessage ownerCheckout = await owner.PostAsJsonAsync(
                new Uri(IntegrationTestRoutes.PAYMENTS_BASE + "/" + ApiRoutes.Payments.CHECKOUT, UriKind.Relative),
                new CreateCheckoutRequest(assetId)))
            {
                ownerCheckout.StatusCode.Should().Be(HttpStatusCode.NotFound, "owner checkout fails the snapshot check before the own-asset check");
            }

            using (HttpResponseMessage ownerVersions = await owner.GetAsync(new Uri(VersionsPath(assetId), UriKind.Relative)))
            {
                ownerVersions.StatusCode.Should().Be(HttpStatusCode.OK, "owner version list");
                using var body = JsonDocument.Parse(await ownerVersions.Content.ReadAsStringAsync());
                body.RootElement.GetArrayLength().Should().Be(1, "the author sees every version");
                body.RootElement[0].GetProperty("id").GetGuid().Should().Be(versionId);
            }
        }
    }

    private sealed record Caller(string Actor, HttpClient Client, HttpStatusCode DownloadStatus, HttpStatusCode CheckoutStatus);

    private async Task<(Guid AssetId, Guid VersionId)> SeedReadyOnlyFirstVersionAsync(string ownerUsername, string title)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User owner = await db.Users.SingleAsync(u => u.Username == ownerUsername);
        Category category = NewCategory();
        var asset = new Asset { Id = Guid.NewGuid(), AuthorId = owner.Id, CategoryId = category.Id, Title = title, Description = "", Price = 1m, CreatedAt = DateTimeOffset.UtcNow };
        AssetVersion version = NewReadyVersion(asset.Id, new string('d', 64));
        var workspace = new AssetDraftWorkspace { Id = Guid.NewGuid(), AssetId = asset.Id, AssetVersionId = version.Id, WorkspaceVersionScopeKey = version.Id, ScopeId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        db.Categories.Add(category);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        db.AssetDraftWorkspaces.Add(workspace);
        db.ModerationSubmissions.Add(new ModerationSubmission
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            OwnerUserId = owner.Id,
            ContentSha256 = version.ContentSha256,
            DeclarationRevision = 1,
            MaterialMetadataRevision = 1,
            SellerEvidenceRevision = 0,
            SellerEvidenceDigest = new string('e', 64),
            PolicyVersion = ReviewAnalysisPolicy.KEY,
            State = ModerationSubmissionState.SUBMITTED,
            CaseRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (asset.Id, version.Id);
    }

    private async Task<(Guid AssetId, Guid VersionId)> SeedApprovedControlAsync(string ownerUsername, string title)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User owner = await db.Users.SingleAsync(u => u.Username == ownerUsername);
        Category category = NewCategory();
        var asset = new Asset { Id = Guid.NewGuid(), AuthorId = owner.Id, CategoryId = category.Id, Title = title, Description = "", Price = 1m, CreatedAt = DateTimeOffset.UtcNow };
        AssetVersion version = NewReadyVersion(asset.Id, new string('f', 64));
        db.Categories.Add(category);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        await TrustedPublicationFixture.AttachTrustedApprovedPublication(db, asset, version, owner, category, title);
        await db.SaveChangesAsync();
        return (asset.Id, version.Id);
    }

    private static Category NewCategory() => new()
    {
        Id = Guid.NewGuid(),
        Name = "First version",
        Slug = "first-version-" + Guid.NewGuid().ToString("N"),
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static AssetVersion NewReadyVersion(Guid assetId, string sha)
    {
        AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        return new AssetVersion
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "first-version/" + Guid.NewGuid().ToString("N") + "/source.bin",
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

    private async Task AssertNoCheckoutIntentAsync(Guid assetId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.CheckoutIntents.AsNoTracking().CountAsync(i => i.AssetId == assetId)).Should().Be(0);
    }

    private static async Task<List<Guid>> ListedIdsAsync(HttpResponseMessage response)
    {
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("items").EnumerateArray().Select(item => item.GetProperty("id").GetGuid()).ToList();
    }

    private static string CatalogUrl(string search) =>
        ASSETS_BASE + ApiRoutes.Assets.LIST + "?page=1&pageSize=20&search=" + Uri.EscapeDataString(search);

    private static string AssetPath(Guid assetId) =>
        ASSETS_BASE + "/" + ApiRoutes.Assets.ID.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal);

    private static string VersionsPath(Guid assetId) =>
        ASSETS_BASE + "/" + ApiRoutes.Assets.VERSIONS.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal);

    private static string AssetDownloadPath(Guid assetId) =>
        ASSETS_BASE + "/" + ApiRoutes.Assets.DOWNLOAD.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal);

    private static string VersionDownloadPath(Guid assetId, Guid versionId) =>
        ASSETS_BASE + "/" + ApiRoutes.Assets.VERSION_DOWNLOAD
            .Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal)
            .Replace("{versionId:guid}", versionId.ToString(), StringComparison.Ordinal);
}
