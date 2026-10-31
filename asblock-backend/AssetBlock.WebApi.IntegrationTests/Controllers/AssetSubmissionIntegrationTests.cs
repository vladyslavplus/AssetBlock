using System.Net;
using System.Net.Http.Json;
using AssetBlock.Domain.Core.Constants;
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
public sealed class AssetSubmissionIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Submit_WhenCurrentAnalysisIsUnavailable_ShouldReturnSellerVisibleConflictReason()
    {
        (HttpClient client, var username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var operationId = Guid.NewGuid();
        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            User owner = await db.Users.SingleAsync(u => u.Username == username);
            var category = new Category { Id = Guid.NewGuid(), Name = "Submission " + assetId.ToString("N"), Slug = "submission-" + assetId.ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
            AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
            db.Categories.Add(category);
            db.Assets.Add(new Asset { Id = assetId, AuthorId = owner.Id, CategoryId = category.Id, Title = "Awaiting analysis", Description = "", Price = 0, CreatedAt = DateTimeOffset.UtcNow });
            db.AssetVersions.Add(new AssetVersion
            {
                Id = versionId,
                AssetId = assetId,
                VersionNumber = 1,
                IsCurrent = true,
                StorageKey = $"assets/{assetId:N}/source.bin",
                FileName = "source.zip",
                ContentLength = 1,
                ContentSha256 = new string('a', 64),
                ReleaseNotes = "Initial release",
                LicenseCode = license.Code,
                LicenseTemplateVersion = license.TemplateVersion,
                LicenseDisplayName = license.DisplayName,
                LicenseTerms = license.TermsPlainText,
                ProcessingStatus = AssetVersionProcessingStatus.READY,
                ProcessingUpdatedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.AssetDraftWorkspaces.Add(new AssetDraftWorkspace
            {
                Id = workspaceId,
                AssetId = assetId,
                AssetVersionId = versionId,
                WorkspaceVersionScopeKey = versionId,
                ScopeId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        HttpResponseMessage response = await client.PostAsJsonAsync($"/api/assets/{assetId}/versions/{versionId}/submissions",
            new { workspaceId, expectedWorkspaceRevision = 1, expectedCaseRevision = 1, operationId });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain(ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE);
        body.Should().Contain(ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE));
        await using AsyncServiceScope verificationScope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext verificationDb = verificationScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await verificationDb.ModerationSubmissions.AnyAsync(s => s.AssetId == assetId)).Should().BeFalse();
        (await verificationDb.JsonMutationIdempotencyRecords.SingleAsync(r => r.OperationId == operationId)).ResultJson
            .Should().Contain(ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE);
    }

    [Theory]
    [InlineData("POST", "responses")]
    [InlineData("PUT", "evidence")]
    [InlineData("GET", "responses")]
    public async Task SellerEvidence_WhenAnonymous_ShouldRequireAuthentication(string method, string endpoint)
    {
        using HttpClient client = fixture.Factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/assets/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/{endpoint}");
        if (method != "GET")
        { request.Content = JsonContent.Create(new { }); }
        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("POST", "responses")]
    [InlineData("PUT", "evidence")]
    public async Task SellerEvidence_WhenInputIsInvalid_ShouldReturnValidationProblem(string method, string endpoint)
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using var request = new HttpRequestMessage(new HttpMethod(method), $"/api/assets/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/{endpoint}")
        {
            Content = JsonContent.Create(new
            {
                workspaceId = Guid.Empty,
                reportId = Guid.Empty,
                operationId = Guid.Empty,
                findingId = Guid.Empty,
                expectedWorkspaceRevision = -1,
                expectedEvidenceRevision = -1,
                responseType = "NO_EVIDENCE",
                text = new string('x', 2001)
            })
        };
        using HttpResponseMessage response = await client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_VALIDATION_FAILED);
    }

    [Fact]
    public async Task SellerEvidence_WhenForeignVersionIsRequested_ShouldNotExposeHistory()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using HttpResponseMessage response = await client.GetAsync($"/api/assets/{Guid.NewGuid()}/versions/{Guid.NewGuid()}/responses");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeTrue();
    }
}
