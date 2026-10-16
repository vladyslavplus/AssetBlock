using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class UsersControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task ListSocialPlatforms_ShouldReturnOkWithSeededPlatforms()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/users/social-platforms", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("GitHub");
    }

    [Fact]
    public async Task GetMe_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/users/me", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMe_WithAuth_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/users/me", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        UserProfileDto? profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(IntegrationTestAuth.JsonOptions);
        profile.Should().NotBeNull();
        profile.Username.Should().NotBeNullOrWhiteSpace();
        profile.Role.Should().Be(AppRoles.USER);
    }

    [Fact]
    public async Task GetByUsername_WhenUserExists_ShouldReturnOk()
    {
        (HttpClient authClient, var username) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        _ = authClient;

        HttpClient anonymous = fixture.Factory.CreateClient();
        HttpResponseMessage response = await anonymous.GetAsync(new Uri($"/api/users/{Uri.EscapeDataString(username)}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        UserProfileDto? profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(IntegrationTestAuth.JsonOptions);
        profile.Should().NotBeNull();
        profile.Username.Should().Be(username);
    }

    [Fact]
    public async Task ListNotifications_WithAuth_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/users/me/notifications?page=1&pageSize=10", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("items").Should().NotBeNull();
    }

    [Fact]
    public async Task MarkNotificationRead_WithAuth_WhenMissing_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var missingId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        HttpResponseMessage response = await client.PatchAsync(
            new Uri($"/api/users/me/notifications/{missingId}/read", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MarkNotificationUnread_WithAuth_WhenMissing_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var missingId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        HttpResponseMessage response = await client.PatchAsync(
            new Uri($"/api/users/me/notifications/{missingId}/unread", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MarkAllNotificationsRead_WithAuth_ShouldReturnOkWithCount()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PostAsync(
            new Uri("/api/users/me/notifications/read-all", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("updatedCount");
    }

    [Fact]
    public async Task UpdateMe_WithAuth_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PatchAsJsonAsync(
            new Uri("/api/users/me", UriKind.Relative),
            new { bio = "Integration test bio." });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateSocials_WithAuth_EmptyLinks_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PutAsJsonAsync(
            new Uri("/api/users/me/socials", UriKind.Relative),
            new UpdateUserSocialLinksRequest { Links = [] });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetMyAssetProcessingJobs_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/assets/{Guid.NewGuid()}/processing-jobs", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMyAssetProcessingJobs_WithAuth_WhenAssetNotFound_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/assets/{Guid.NewGuid()}/processing-jobs", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMyAssetVersionProcessingJobs_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/asset-versions/{Guid.NewGuid()}/processing-jobs", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMyAssetVersionProcessingJobs_WithAuth_WhenVersionNotFound_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/asset-versions/{Guid.NewGuid()}/processing-jobs", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMyAsset_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/assets/{Guid.NewGuid()}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMyAsset_WithAuth_WhenMissing_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/assets/{Guid.NewGuid()}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListingCopilot_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        var versionId = Guid.NewGuid();
        HttpResponseMessage get = await client.GetAsync(new Uri($"/api/users/me/asset-versions/{versionId}/listing-copilot", UriKind.Relative));
        HttpResponseMessage post = await client.PostAsync(new Uri($"/api/users/me/asset-versions/{versionId}/listing-copilot", UriKind.Relative), null);

        get.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        post.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListingCopilotPost_WhenEmailUnverified_ShouldReturn403()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PostAsync(
            new Uri($"/api/users/me/asset-versions/{Guid.NewGuid()}/listing-copilot", UriKind.Relative),
            null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListingCopilot_WithAuth_WhenVersionMissing_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var versionId = Guid.NewGuid();
        HttpResponseMessage get = await client.GetAsync(new Uri($"/api/users/me/asset-versions/{versionId}/listing-copilot", UriKind.Relative));
        HttpResponseMessage post = await client.PostAsync(new Uri($"/api/users/me/asset-versions/{versionId}/listing-copilot", UriKind.Relative), null);

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnalysisFindings_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/users/me/asset-versions/{Guid.NewGuid()}/analysis/reports/{Guid.NewGuid()}/findings?page=1", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnalysisRetry_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/users/me/assets/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/analysis-retries", UriKind.Relative),
            new
            {
                operationId = Guid.NewGuid(),
                expectedRunId = Guid.NewGuid(),
                expectedWorkspaceRevision = 1,
                expectedPolicyDigest = new string('a', 64),
                expectedInputDigest = new string('b', 64)
            });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AnalysisRetry_WhenEmailUnverified_ShouldReturn403()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/users/me/assets/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/analysis-retries", UriKind.Relative),
            new
            {
                operationId = Guid.NewGuid(),
                expectedRunId = Guid.NewGuid(),
                expectedWorkspaceRevision = 1,
                expectedPolicyDigest = new string('a', 64),
                expectedInputDigest = new string('b', 64)
            });

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnalysisRetry_WhenExpectationIsMalformed_ShouldReturn400()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PostAsJsonAsync(
            new Uri($"/api/users/me/assets/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/analysis-retries", UriKind.Relative),
            new
            {
                operationId = Guid.Empty,
                expectedRunId = Guid.Empty,
                expectedWorkspaceRevision = 0,
                expectedPolicyDigest = "bad",
                expectedInputDigest = "bad"
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnalysisFindings_WhenOwnerDoesNotOwnVersion_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/users/me/asset-versions/{Guid.NewGuid()}/analysis/reports/{Guid.NewGuid()}/findings?page=1", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnalysisFindings_WhenFinalizedHeaderAndMatchingAdmissionHaveNoBoundRun_ShouldReturn404()
    {
        (HttpClient client, var username) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        Guid assetId;
        Guid versionId;
        Guid reportId;
        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            User owner = await db.Users.SingleAsync(u => u.Username == username);
            var suffix = Guid.NewGuid().ToString("N");
            DateTimeOffset now = DateTimeOffset.UtcNow;
            Category category = new()
            {
                Id = Guid.NewGuid(),
                Name = $"Analysis test {suffix}",
                Slug = $"analysis-test-{suffix}",
                CreatedAt = now
            };
            assetId = Guid.NewGuid();
            versionId = Guid.NewGuid();
            reportId = Guid.NewGuid();
            var contentHash = new string('a', 64);
            var reportDigest = new string('b', 64);
            var pythonReportId = new string('c', 64);
            var inputDigest = new string('d', 64);
            Asset asset = new()
            {
                Id = assetId,
                AuthorId = owner.Id,
                CategoryId = category.Id,
                Title = "Unbound analysis report",
                Description = "",
                Price = 0,
                CreatedAt = now
            };
            AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
            AssetVersion version = new()
            {
                Id = versionId,
                AssetId = assetId,
                VersionNumber = 1,
                IsCurrent = true,
                StorageKey = $"assets/{assetId:N}/v1.bin",
                FileName = "package.zip",
                ContentLength = 1,
                ContentSha256 = contentHash,
                ReleaseNotes = "Initial release",
                LicenseCode = license.Code,
                LicenseTemplateVersion = license.TemplateVersion,
                LicenseDisplayName = license.DisplayName,
                LicenseTerms = license.TermsPlainText,
                ProcessingStatus = AssetVersionProcessingStatus.READY,
                ProcessingUpdatedAt = now,
                CreatedAt = now
            };
            CodeAnalysisReportHeader report = new()
            {
                Id = reportId,
                AssetId = assetId,
                AssetVersionId = versionId,
                ContentSha256 = contentHash,
                PolicyVersion = "code-analysis-policy-v1",
                InputRevision = 1,
                Purpose = CodeAnalysisReportPurpose.PRODUCTION,
                CanAuthorizePublication = false,
                IsFinalized = true,
                FinalizedAt = now,
                ReportSchemaVersion = 1,
                PythonReportId = pythonReportId,
                ReportDigest = reportDigest,
                CreatedAt = now
            };
            CodeAnalysisReportAdmission admission = new()
            {
                Id = Guid.NewGuid(),
                ReportId = reportId,
                AdmissionKind = CodeAnalysisAdmissionKind.SERVER_VALIDATED,
                AdmittedBy = "assetblock-dotnet-finalizer",
                AdmittedAt = now,
                ReportDigest = reportDigest,
                PythonReportId = pythonReportId,
                InputDigest = inputDigest,
                ServiceKey = "code-analysis",
                ReservationSlotNumber = 0,
                ReservationGeneration = 1,
                ReservationLeaseTokenDigest = new string('e', 64),
                CanAuthorizePublication = false,
                PublicationAuthorityReason = "Research evaluation only.",
                CreatedAt = now
            };
            db.Categories.Add(category);
            db.Assets.Add(asset);
            db.AssetVersions.Add(version);
            db.CodeAnalysisReportHeaders.Add(report);
            db.CodeAnalysisReportAdmissions.Add(admission);
            await db.SaveChangesAsync();
        }

        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/users/me/asset-versions/{versionId}/analysis/reports/{reportId}/findings?page=1", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
