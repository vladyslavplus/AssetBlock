using System.Net;
using System.Net.Http.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Services;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class RestrictedReviewEvidenceIntegrationTests(IntegrationTestFixture fixture)
{
    private const string PRIVATE_EXCERPT = "restricted fixture excerpt sentinel";
    private sealed record CaseSeed(Guid SubmissionId, Guid VersionId, Guid FindingId);
    private async Task<CaseSeed> SeedCase(string ownerUsername)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User owner = await db.Users.SingleAsync(u => u.Username == ownerUsername);
        var category = new Category { Id = Guid.NewGuid(), Name = "Evidence", Slug = "evidence-" + Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
        var asset = new Asset { Id = Guid.NewGuid(), AuthorId = owner.Id, CategoryId = category.Id, Title = "Evidence fixture", Description = "", Price = 1m, CreatedAt = DateTimeOffset.UtcNow };
        AssetLicenseTemplate license = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL);
        var version = new AssetVersion
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "evidence-fixture/" + Guid.NewGuid().ToString("N") + "/source.bin",
            FileName = "source.zip",
            ContentLength = 1,
            ContentSha256 = new string('a', 64),
            ReleaseNotes = "Fixture",
            LicenseCode = license.Code,
            LicenseTemplateVersion = license.TemplateVersion,
            LicenseDisplayName = license.DisplayName,
            LicenseTerms = license.TermsPlainText,
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var workspace = new AssetDraftWorkspace { Id = Guid.NewGuid(), AssetId = asset.Id, AssetVersionId = version.Id, WorkspaceVersionScopeKey = version.Id, ScopeId = Guid.NewGuid(), CreatedAt = DateTimeOffset.UtcNow };
        var report = new CodeAnalysisReportHeader
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            PolicyVersion = ReviewAnalysisPolicy.KEY,
            InputRevision = 1,
            Purpose = CodeAnalysisReportPurpose.FIXTURE,
            ReviewEligible = false,
            IsFinalized = true,
            ReportSchemaVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var finding = new CodeAnalysisReportFinding
        {
            Id = Guid.NewGuid(),
            ReportId = report.Id,
            FindingKey = "restricted-fixture",
            FindingType = "POTENTIAL_MATCH",
            OwnScopePath = "own.py",
            OwnExcerpt = PRIVATE_EXCERPT,
            ForeignSensitivity = CodeAnalysisFindingSensitivity.PRIVATE,
            ReasonText = "Fixture evidence",
            RequestedAction = "Review",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var submission = new ModerationSubmission
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = version.Id,
            OwnerUserId = owner.Id,
            ContentSha256 = version.ContentSha256,
            CodeAnalysisReportHeaderId = report.Id,
            DeclarationRevision = 1,
            MaterialMetadataRevision = 1,
            SellerEvidenceRevision = 0,
            SellerEvidenceDigest = new string('b', 64),
            PolicyVersion = ReviewAnalysisPolicy.KEY,
            State = ModerationSubmissionState.SUBMITTED,
            CaseRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Categories.Add(category);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        db.AssetDraftWorkspaces.Add(workspace);
        db.CodeAnalysisReportHeaders.Add(report);
        db.CodeAnalysisReportFindings.Add(finding);
        db.ModerationSubmissions.Add(submission);
        await db.SaveChangesAsync();
        return new(submission.Id, version.Id, finding.Id);
    }
    private static string EvidenceUrl(CaseSeed seed) => ApiRoutes.Moderation.CASE_EVIDENCE.Replace("{id:guid}", seed.SubmissionId.ToString(), StringComparison.Ordinal)
        .Replace("{kind}", nameof(RestrictedEvidenceKind.OWN_EXCERPT), StringComparison.Ordinal) + $"?findingId={seed.FindingId}";
    [Fact]
    public async Task Assignment_WhenCalledThroughHttp_ShouldEnforceRoleConflictsAndNoStore()
    {
        (HttpClient owner, var username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        {
            CaseSeed seed = await SeedCase(username);
            (HttpClient moderator, var moderatorName) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
            (HttpClient other, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
            using (moderator)
            using (other)
            {
                var queuePath = "/" + ApiRoutes.Moderation.QUEUE;
                var claimPath = "/" + ApiRoutes.Moderation.CLAIM.Replace("{id:guid}", seed.SubmissionId.ToString(), StringComparison.Ordinal);
                var releasePath = "/" + ApiRoutes.Moderation.RELEASE.Replace("{id:guid}", seed.SubmissionId.ToString(), StringComparison.Ordinal);
                using HttpClient anonymous = fixture.Factory.CreateClient();
                using HttpResponseMessage unauthenticated = await anonymous.GetAsync(queuePath);
                unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
                unauthenticated.Headers.CacheControl!.NoStore.Should().BeTrue();
                (HttpClient admin, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.ADMIN);
                using (admin)
                using (HttpResponseMessage adminDenied = await admin.GetAsync(queuePath))
                { adminDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden); }
                using HttpResponseMessage forbidden = await owner.GetAsync(queuePath);
                forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                using HttpResponseMessage queue = await moderator.GetAsync(queuePath);
                queue.StatusCode.Should().Be(HttpStatusCode.OK);
                queue.Headers.CacheControl!.NoStore.Should().BeTrue();
                (await queue.Content.ReadAsStringAsync()).Should().Contain(seed.SubmissionId.ToString()).And.NotContain(PRIVATE_EXCERPT);
                using HttpResponseMessage bounds = await moderator.GetAsync(queuePath + "?pageSize=101");
                bounds.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                using HttpResponseMessage badCursor = await moderator.GetAsync(queuePath + "?cursor=tampered");
                badCursor.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                using HttpResponseMessage claim = await moderator.PostAsJsonAsync(claimPath, new { operationId = Guid.NewGuid(), expectedCaseRevision = 1 });
                claim.StatusCode.Should().Be(HttpStatusCode.OK);
                claim.Headers.CacheControl!.NoStore.Should().BeTrue();
                using HttpResponseMessage contested = await other.PostAsJsonAsync(claimPath, new { operationId = Guid.NewGuid(), expectedCaseRevision = 2 });
                contested.StatusCode.Should().Be(HttpStatusCode.Conflict);
                (await contested.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_CASE_HELD);
                using HttpResponseMessage release = await moderator.PostAsJsonAsync(releasePath, new { operationId = Guid.NewGuid(), expectedCaseRevision = 2 });
                release.StatusCode.Should().Be(HttpStatusCode.OK);
                release.Headers.CacheControl!.NoStore.Should().BeTrue();
                using HttpResponseMessage stale = await moderator.PostAsJsonAsync(claimPath, new { operationId = Guid.NewGuid(), expectedCaseRevision = 1 });
                stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
                stale.Headers.CacheControl!.NoStore.Should().BeTrue();
                (await stale.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_MODERATION_CASE_STALE);
                await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Users.Where(u => u.Username == moderatorName).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
                using HttpResponseMessage revoked = await moderator.GetAsync(queuePath);
                revoked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
        }
    }
    [Fact]
    public async Task Evidence_WhenModeratorReads_ShouldHaveCommittedSafeAuditAndNoStoreBeforeBody()
    {
        (HttpClient owner, var username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        {
            CaseSeed seed = await SeedCase(username);
            (HttpClient moderator, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
            using (moderator)
            using (var request = new HttpRequestMessage(HttpMethod.Get, EvidenceUrl(seed)))
            using (HttpResponseMessage response = await moderator.SendAsync(request, HttpCompletionOption.ResponseHeadersRead))
            {
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                response.Headers.CacheControl.Should().Match<System.Net.Http.Headers.CacheControlHeaderValue>(c => c.Private && c.NoStore);
                await using AsyncServiceScope observer = fixture.Factory.Services.CreateAsyncScope();
                ApplicationDbContext db = observer.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                AuditLog audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == AuditActions.RESTRICTED_EVIDENCE_READ && a.ResourceId == seed.SubmissionId.ToString());
                audit.MetadataJson.Should().Contain("OWN_EXCERPT").And.NotContain(PRIVATE_EXCERPT);
                RestrictedEvidenceDto? evidence = await response.Content.ReadFromJsonAsync<RestrictedEvidenceDto>();
                evidence!.Content.Should().Be(PRIVATE_EXCERPT);
                using HttpResponseMessage invalidKind = await moderator.GetAsync($"/api/moderation/submissions/{seed.SubmissionId}/evidence/999");
                invalidKind.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                using HttpResponseMessage missing = await moderator.GetAsync($"/api/moderation/submissions/{Guid.NewGuid()}/evidence/OWN_EXCERPT?findingId={seed.FindingId}");
                missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
                await db.AssetVersions.Where(v => v.Id == seed.VersionId).ExecuteUpdateAsync(s => s.SetProperty(v => v.SafetyRestriction, AssetVersionSafetyRestriction.INFECTED)
                    .SetProperty(v => v.SafetyRestrictionReason, "MALWARE_DETECTED").SetProperty(v => v.SafetyRestrictedAt, DateTimeOffset.UtcNow));
                using HttpResponseMessage restricted = await moderator.GetAsync(EvidenceUrl(seed));
                restricted.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await restricted.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_EVIDENCE_SECURITY_RESTRICTED).And.NotContain(PRIVATE_EXCERPT);
            }
        }
    }
    [Theory]
    [InlineData("owner")]
    [InlineData("admin")]
    [InlineData("foreign")]
    [InlineData("revoked")]
    [InlineData("anonymous")]
    public async Task Evidence_WhenCallerCannotReview_ShouldDenyWithoutRevealingContent(string actor)
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        using (owner)
        {
            CaseSeed seed = await SeedCase(ownerName);
            HttpClient client;
            if (actor == "owner")
            { client = owner; }
            else if (actor == "anonymous")
            { client = fixture.Factory.CreateClient(); }
            else
            {
                (client, var username) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, actor == "admin" ? AppRoles.ADMIN : actor == "foreign" ? AppRoles.USER : AppRoles.MODERATOR);
                if (actor == "revoked")
                {
                    await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.Users.Where(u => u.Username == username).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
                }
            }
            using HttpResponseMessage response = await client.GetAsync(EvidenceUrl(seed));
            response.StatusCode.Should().Be(actor == "anonymous" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden);
            (await response.Content.ReadAsStringAsync()).Should().NotContain(PRIVATE_EXCERPT);
            if (!ReferenceEquals(client, owner))
            { client.Dispose(); }
        }
    }
    [Fact]
    public async Task Evidence_WhenMandatoryAuditWriterFails_ShouldReturnUnavailableWithoutText()
    {
        (HttpClient owner, var username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using (owner)
        {
            CaseSeed seed = await SeedCase(username);
            using WebApplicationFactory<Program> failing = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureServices(services => services.AddScoped<IAuditWriter>(sp =>
                new FailingEvidenceAuditWriter(new AuditWriter(sp.GetRequiredService<IAuditStore>(), sp.GetRequiredService<IAuditContextAccessor>(), sp.GetRequiredService<ILogger<AuditWriter>>())))));
            (HttpClient reviewer, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(failing, AppRoles.MODERATOR);
            using (reviewer)
            using (HttpResponseMessage response = await reviewer.GetAsync(EvidenceUrl(seed)))
            {
                response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
                (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_EVIDENCE_AUDIT_UNAVAILABLE).And.NotContain(PRIVATE_EXCERPT);
            }
        }
    }
    [Fact]
    public async Task Coverage_WhenEndpointsRejectUnauthorizedStaleOrInvalidInput_ShouldReturnStandardProblems()
    {
        (HttpClient owner, var username) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        using (owner)
        {
            CaseSeed seed = await SeedCase(username);
            var requestId = Guid.NewGuid();
            Guid assetId;
            Guid workspaceId;
            Guid reportId;
            await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
            {
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                ModerationSubmission submission = await db.ModerationSubmissions.SingleAsync(s => s.Id == seed.SubmissionId);
                assetId = submission.AssetId;
                workspaceId = submission.WorkspaceId;
                reportId = submission.CodeAnalysisReportHeaderId!.Value;
                db.CoverageReviewRequests.Add(new CoverageReviewRequest
                {
                    Id = requestId,
                    AssetId = assetId,
                    AssetVersionId = seed.VersionId,
                    WorkspaceId = workspaceId,
                    ReportId = reportId,
                    OwnerUserId = submission.OwnerUserId,
                    ContentSha256 = submission.ContentSha256,
                    DeclarationRevision = 1,
                    MaterialRevision = 1,
                    SellerEvidenceRevision = 0,
                    PolicyVersion = ReviewAnalysisPolicy.KEY,
                    PolicyDigest = ReviewAnalysisPolicy.DIGEST,
                    TupleDigest = new string('d', 64),
                    RequestedWorkspaceRevision = 1,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
            }
            var input = new CoverageAssessmentRequest(Guid.NewGuid(), 99, new string('d', 64), CoverageAssessmentOutcome.BLOCKED,
                ["Python"], [], false, "Read scope", "", "Private rationale sentinel");
            var assessmentPath = ApiRoutes.Moderation.COVERAGE_ASSESSMENT.Replace("{id:guid}", requestId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage self = await owner.PostAsJsonAsync(assessmentPath, input);
            self.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (HttpClient moderator, var moderatorName) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
            using (moderator)
            {
                using HttpResponseMessage queue = await moderator.GetAsync(ApiRoutes.Moderation.COVERAGE_REQUESTS + "?page=1");
                queue.StatusCode.Should().Be(HttpStatusCode.OK);
                queue.Headers.CacheControl!.NoStore.Should().BeTrue();
                var evidencePath = ApiRoutes.Moderation.COVERAGE_EVIDENCE.Replace("{id:guid}", requestId.ToString(), StringComparison.Ordinal).Replace("{kind}", nameof(RestrictedEvidenceKind.OWN_EXCERPT), StringComparison.Ordinal);
                using HttpResponseMessage read = await moderator.GetAsync(evidencePath + $"?findingId={seed.FindingId}");
                read.StatusCode.Should().Be(HttpStatusCode.OK);
                read.Headers.CacheControl!.NoStore.Should().BeTrue();
                using HttpResponseMessage stale = await moderator.PostAsJsonAsync(assessmentPath, input);
                stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
                (await stale.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_COVERAGE_REVIEW_CONFLICT).And.NotContain(input.Rationale);
                using HttpResponseMessage invalid = await moderator.PostAsJsonAsync(assessmentPath, input with { Rationale = new string('x', 2001) });
                invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
                await using AsyncServiceScope revoke = fixture.Factory.Services.CreateAsyncScope();
                ApplicationDbContext db = revoke.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                await db.Users.Where(u => u.Username == moderatorName).ExecuteUpdateAsync(s => s.SetProperty(u => u.Role, AppRoles.USER));
                using HttpResponseMessage revoked = await moderator.PostAsJsonAsync(assessmentPath, input);
                revoked.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
            (HttpClient admin, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.ADMIN);
            using (admin)
            using (HttpResponseMessage denied = await admin.PostAsJsonAsync(assessmentPath, input))
            { denied.StatusCode.Should().Be(HttpStatusCode.Forbidden); }
            var requestPath = AuthenticatedEncryptedUploadAcceptanceTests.ReadReviewEndpoint("coverage", assetId, seed.VersionId).Path;
            var requestInput = new CoverageReviewCreateRequest(Guid.NewGuid(), workspaceId, reportId, 99);
            using HttpResponseMessage staleRequest = await owner.PostAsJsonAsync(requestPath, requestInput);
            staleRequest.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await staleRequest.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_COVERAGE_REVIEW_CONFLICT);
            using HttpResponseMessage invalidRequest = await owner.PostAsJsonAsync(requestPath, requestInput with { OperationId = Guid.Empty });
            invalidRequest.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (HttpClient foreign, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
            using (foreign)
            using (HttpResponseMessage denied = await foreign.PostAsJsonAsync(requestPath, requestInput))
            { denied.StatusCode.Should().Be(HttpStatusCode.Forbidden); }
        }
    }
    private sealed class FailingEvidenceAuditWriter(IAuditWriter inner) : IAuditWriter
    {
        public Task Write(AuditEvent entry, CancellationToken cancellationToken = default) => entry.Action == AuditActions.RESTRICTED_EVIDENCE_READ
            ? Task.FromException(new InvalidOperationException("Evidence auditing unavailable.")) : inner.Write(entry, cancellationToken);
        public Task WriteBestEffort(AuditEvent entry, CancellationToken cancellationToken = default) => inner.WriteBestEffort(entry, cancellationToken);
    }
}
