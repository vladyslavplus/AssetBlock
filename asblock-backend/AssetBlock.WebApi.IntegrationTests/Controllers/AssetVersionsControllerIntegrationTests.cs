using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

/// <summary>
/// HTTP-level matrix for asset version lifecycle: version listing authorization, publish authorization,
/// download entitlement, and the Range-header 416 contract.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class AssetVersionsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    private IServiceScopeFactory ScopeFactory => fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();

    [Fact]
    public async Task ListVersions_WhenAssetActive_AsAnonymous_ShouldReturnOk()
    {
        var (_, authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, List<Guid> versionIds) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 2);

        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri($"/api/assets/{assetId}/versions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var raw = await response.Content.ReadAsStringAsync();
        raw.ToLowerInvariant().Should().NotContain("storagekey");
        var versions = await response.Content.ReadFromJsonAsync<List<AssetVersionSummaryResponse>>();
        versions.Should().NotBeNull();
        versions!.Select(v => v.Id).Should().BeEquivalentTo(versionIds);
        versions.Should().OnlyContain(v =>
            !string.IsNullOrWhiteSpace(v.ContentSha256) &&
            !string.IsNullOrWhiteSpace(v.FileName) &&
            !string.IsNullOrWhiteSpace(v.ReleaseNotes) &&
            v.ContentLength > 0 &&
            !string.IsNullOrWhiteSpace(v.License.Code) &&
            !string.IsNullOrWhiteSpace(v.License.Terms));
    }

    [Fact]
    public async Task ListVersions_WhenAssetMissing_ShouldReturn404()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri($"/api/assets/{Guid.NewGuid()}/versions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task ListVersions_WhenAssetSoftDeleted_AsAnonymous_ShouldReturn404()
    {
        var (_, authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        var (assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1, deleted: true);

        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri($"/api/assets/{assetId}/versions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListVersions_WhenAssetSoftDeleted_AsUnrelatedUser_ShouldReturn404()
    {
        var (_, authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1, deleted: true);

        (HttpClient stranger, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await stranger.GetAsync(new Uri($"/api/assets/{assetId}/versions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListVersions_WhenAssetSoftDeleted_AsBuyer_ShouldReturnOk()
    {
        var (_, authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, List<Guid> versionIds) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1, deleted: true);

        (HttpClient buyer, var buyerUsername) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var buyerId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, buyerUsername);
        await AssetVersionsSeed.SeedPurchaseAsync(ScopeFactory, buyerId, assetId, versionIds[0]);

        var response = await buyer.GetAsync(new Uri($"/api/assets/{assetId}/versions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = await response.Content.ReadFromJsonAsync<List<AssetVersionSummaryResponse>>();
        versions.Should().NotBeNull();
        versions!.Should().ContainSingle(v => v.Id == versionIds[0]);
    }

    [Fact]
    public async Task ListVersions_WhenAssetSoftDeleted_AsAuthor_ShouldReturnOk()
    {
        (HttpClient author, var authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, List<Guid> versionIds) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1, deleted: true);

        var response = await author.GetAsync(new Uri($"/api/assets/{assetId}/versions", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = await response.Content.ReadFromJsonAsync<List<AssetVersionSummaryResponse>>();
        versions.Should().NotBeNull();
        versions!.Should().ContainSingle(v => v.Id == versionIds[0]);
    }

    [Fact]
    public async Task PublishVersion_WhenUnauthenticated_ShouldReturn401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsync(
            new Uri($"/api/assets/{Guid.NewGuid()}/versions", UriKind.Relative),
            BuildPublishForm(CreateZipArchive("plain"), "PERSONAL", "notes"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task PublishVersion_WhenAuthorUnverified_ShouldReturn403EmailNotVerified()
    {
        (HttpClient author, var authorUsername) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1);

        var response = await author.PostAsync(
            new Uri($"/api/assets/{assetId}/versions", UriKind.Relative),
            BuildPublishForm(CreateZipArchive("plain"), "PERSONAL", "notes"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_EMAIL_NOT_VERIFIED);
    }

    [Fact]
    public async Task PublishVersion_WhenNonAuthorVerified_ShouldReturn403()
    {
        (HttpClient owner, var ownerUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var ownerId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, ownerUsername);
        (Guid assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, ownerId, versionCount: 1);

        (HttpClient intruder, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var response = await intruder.PostAsync(
            new Uri($"/api/assets/{assetId}/versions", UriKind.Relative),
            BuildPublishForm(CreateZipArchive("plain"), "PERSONAL", "notes"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_FORBIDDEN);
    }

    [Fact]
    public async Task PublishVersion_WhenAssetSoftDeleted_ShouldReturn404()
    {
        (HttpClient author, var authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1, deleted: true);

        var response = await author.PostAsync(
            new Uri($"/api/assets/{assetId}/versions", UriKind.Relative),
            BuildPublishForm(CreateZipArchive("plain"), "PERSONAL", "notes"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task PublishVersion_WhenAuthorVerified_ShouldReturn201AndAppendNextVersion()
    {
        (HttpClient author, var authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, List<Guid> versionIds) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1);

        var zipBytes = CreateZipArchive("v2 payload");
        var expectedSha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipBytes)).ToLowerInvariant();
        const string releaseNotesRaw = "  Second release  ";

        var response = await author.PostAsync(
            new Uri($"/api/assets/{assetId}/versions", UriKind.Relative),
            BuildPublishForm(zipBytes, "COMMERCIAL", releaseNotesRaw));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        response.Headers.Location.Should().NotBeNull();
        var locationPath = response.Headers.Location!.IsAbsoluteUri
            ? response.Headers.Location.AbsolutePath
            : response.Headers.Location.OriginalString;
        locationPath.Equals($"/api/assets/{assetId}/versions", StringComparison.OrdinalIgnoreCase).Should().BeTrue();

        var listResponse = await author.GetAsync(new Uri($"/api/assets/{assetId}/versions", UriKind.Relative));
        listResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var versions = await listResponse.Content.ReadFromJsonAsync<List<AssetVersionSummaryResponse>>();
        versions.Should().NotBeNull();
        versions!.Should().HaveCount(2);
        versions!.Should().ContainSingle(v => v.Id == versionIds[0] && !v.IsCurrent);
        var v2 = versions!.Should()
            .ContainSingle(v => v.VersionNumber == 2 && v.IsCurrent && v.License.Code == "COMMERCIAL")
            .Which;
        v2.ContentSha256.Should().Be(expectedSha256);
        v2.ContentLength.Should().Be(zipBytes.Length);
        v2.ReleaseNotes.Should().Be("Second release");
    }

    [Fact]
    public async Task Download_WhenUnauthenticated_ShouldReturn401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri($"/api/assets/{Guid.NewGuid()}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Download_WhenAssetMissing_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(new Uri($"/api/assets/{Guid.NewGuid()}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_ASSET_NOT_FOUND);
    }

    [Fact]
    public async Task Download_WhenAuthenticatedButNotEntitled_ShouldReturn403()
    {
        var (_, authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1);

        (HttpClient stranger, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await stranger.GetAsync(new Uri($"/api/assets/{assetId}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_PURCHASE_ACCESS_DENIED);
    }

    [Fact]
    public async Task Download_WhenAuthor_ShouldReturn200WithDecryptedContent()
    {
        (HttpClient author, var authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, List<Guid> versionIds) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1);

        var plaintext = "author-owned content"u8.ToArray();
        await SeedVersionContentAsync(versionIds[0], plaintext);

        var response = await author.GetAsync(new Uri($"/api/assets/{assetId}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().BeEquivalentTo(plaintext);
    }

    [Fact]
    public async Task Download_WhenPurchaser_ShouldReturn200WithDecryptedContent()
    {
        var (_, authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, List<Guid> versionIds) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1);

        var plaintext = "purchaser entitled content"u8.ToArray();
        await SeedVersionContentAsync(versionIds[0], plaintext);

        (HttpClient buyer, var buyerUsername) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var buyerId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, buyerUsername);
        await AssetVersionsSeed.SeedPurchaseAsync(ScopeFactory, buyerId, assetId, versionIds[0]);

        var response = await buyer.GetAsync(new Uri($"/api/assets/{assetId}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().BeEquivalentTo(plaintext);
    }

    [Fact]
    public async Task DownloadVersion_WhenPurchaserRequestsVersionOlderThanPurchased_ShouldReturn403()
    {
        var (_, authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, List<Guid> versionIds) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 2);

        (HttpClient buyer, var buyerUsername) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var buyerId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, buyerUsername);
        // Buyer purchased version 2 (index 1); requesting version 1 (an older, pre-purchase version) must be denied.
        await AssetVersionsSeed.SeedPurchaseAsync(ScopeFactory, buyerId, assetId, versionIds[1]);

        var response = await buyer.GetAsync(
            new Uri($"/api/assets/{assetId}/versions/{versionIds[0]}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_PURCHASE_ACCESS_DENIED);
    }

    [Fact]
    public async Task DownloadVersion_WhenPurchaserRequestsNewerEntitledVersion_ShouldReturn200()
    {
        var (_, authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, List<Guid> versionIds) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 2);

        var plaintext = "newer entitled version content"u8.ToArray();
        await SeedVersionContentAsync(versionIds[1], plaintext);

        (HttpClient buyer, var buyerUsername) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var buyerId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, buyerUsername);
        // Buyer purchased version 1 (index 0); later versions of the same asset remain entitled.
        await AssetVersionsSeed.SeedPurchaseAsync(ScopeFactory, buyerId, assetId, versionIds[0]);

        var response = await buyer.GetAsync(
            new Uri($"/api/assets/{assetId}/versions/{versionIds[1]}/download", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        bytes.Should().BeEquivalentTo(plaintext);
    }

    [Fact]
    public async Task Download_WhenRangeHeaderPresent_ShouldReturn416()
    {
        (HttpClient author, var authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/assets/{assetId}/download");
        request.Headers.Range = new RangeHeaderValue(0, 10);
        var response = await author.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        response.Headers.AcceptRanges.Should().Contain("none");
    }

    [Fact]
    public async Task DownloadVersion_WhenRangeHeaderPresent_ShouldReturn416()
    {
        (HttpClient author, var authorUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var authorId = await AssetVersionsSeed.GetUserIdAsync(ScopeFactory, authorUsername);
        (Guid assetId, List<Guid> versionIds) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(ScopeFactory, authorId, versionCount: 1);

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"/api/assets/{assetId}/versions/{versionIds[0]}/download");
        request.Headers.Range = new RangeHeaderValue(0, 10);
        var response = await author.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.RequestedRangeNotSatisfiable);
        response.Headers.AcceptRanges.Should().Contain("none");
    }

    private async Task SeedVersionContentAsync(Guid versionId, byte[] plaintext)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var encryptionService = scope.ServiceProvider.GetRequiredService<IEncryptionService>();

        // Version storage keys are immutable after insert — seed fake MinIO under the existing key.
        var storageKey = await AssetVersionsSeed.GetVersionStorageKeyAsync(ScopeFactory, versionId);
        await using var plain = new MemoryStream(plaintext, writable: false);
        await using var cipher = new MemoryStream();
        await encryptionService.Encrypt(plain, cipher, CancellationToken.None);

        fixture.Factory.AssetStorage.Seed(storageKey, cipher.ToArray());
    }

    private static byte[] CreateZipArchive(string entryContent)
    {
        using var output = new MemoryStream();
        using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("payload.txt", CompressionLevel.Fastest);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(entryContent);
        }

        return output.ToArray();
    }

    private static MultipartFormDataContent BuildPublishForm(byte[] zipBytes, string licenseCode, string releaseNotes)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(licenseCode), "LicenseCode" },
            { new StringContent(releaseNotes), "ReleaseNotes" }
        };
        var fileContent = new ByteArrayContent(zipBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        form.Add(fileContent, "file", "version.zip");
        return form;
    }

    private sealed record AssetVersionSummaryResponse(
        Guid Id,
        int VersionNumber,
        bool IsCurrent,
        string FileName,
        long ContentLength,
        string ContentSha256,
        string ReleaseNotes,
        DateTimeOffset CreatedAt,
        AssetLicenseSummaryResponse License);

    private sealed record AssetLicenseSummaryResponse(
        string Code,
        string DisplayName,
        string TemplateVersion,
        string Terms);
}
