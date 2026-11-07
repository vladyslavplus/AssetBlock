using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.Constants;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Support;

/// <summary>Caller classes for review-loop access matrices.</summary>
internal enum ReviewCaller
{
    Anonymous,
    Stranger,
    OwnerUser,
    OwnerModerator,
    OtherModerator,
    Admin,
    RevokedModerator
}

/// <summary>One seeded case: the asset, its READY version, the SUBMITTED case, its report and workspace.</summary>
internal sealed record ReviewCase(Guid AssetId, Guid VersionId, Guid SubmissionId, Guid ReportId, Guid WorkspaceId, Guid FindingId, string OwnerName);

/// <summary>Current workspace and seller-evidence revisions of one case.</summary>
internal sealed record ReviewSellerRevisions(long WorkspaceRevision, int EvidenceRevision);

/// <summary>Callers keyed by class. The owner-Moderator client is added lazily once the owner is promoted.</summary>
internal sealed record ReviewCallers(Dictionary<ReviewCaller, HttpClient> Clients, string OwnerName);

/// <summary>
/// One expected call. AuditDelta is the audit rows a handler-reached denial writes (restricted evidence reads are
/// audited even when denied); authorization-filter denials write none. ExpectedCode, when set, must appear in the
/// response body, so two denials with the same status can be told apart by their reason.
/// </summary>
internal sealed record ReviewStep(ReviewCaller Caller, HttpStatusCode Expected, int AuditDelta = 0, string? ExpectedCode = null);

/// <summary>
/// Row counts a write endpoint could touch for one case. A denied or rejected call must leave every field unchanged.
/// </summary>
internal sealed record ReviewCaseSnapshot(
    ModerationSubmissionState State,
    Guid? ClaimHolderId,
    long CaseRevision,
    long WorkspaceRevision,
    int SellerEvidenceRows,
    int SellerResponseRows,
    int DecisionRows,
    int PublicationSnapshotRows,
    int SubmissionRows,
    int CoverageRequestRows,
    int OpenCoverageRequestRows,
    long CoverageRequestRevisionSum,
    int CoverageAssessmentRows,
    int OutboxRows,
    int AuditRows);

/// <summary>Seeding, caller creation and step execution shared by the review access matrices.</summary>
internal sealed class ReviewAccessMatrix(IntegrationTestFixture fixture)
{
    /// <summary>Largest queue page the endpoint accepts, so a full walk stays short.</summary>
    private const int QUEUE_PAGE_SIZE = 100;

    /// <summary>Upper bound on pages walked, so a looping cursor fails the test instead of hanging it.</summary>
    private const int MAX_PAGES = 200;

    public async Task<ReviewCallers> CreateCallersAsync()
    {
        (HttpClient owner, var ownerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient other, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        (HttpClient stranger, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        (HttpClient admin, _) = await IntegrationTestAuth.RegisterAdminAndAuthenticateAsync(fixture.Factory);
        (HttpClient revoked, var revokedName) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(fixture.Factory, AppRoles.MODERATOR);
        // The revoked moderator keeps a token issued while the role was Moderator.
        await SetRoleAsync(revokedName, AppRoles.USER);
        Dictionary<ReviewCaller, HttpClient> clients = new()
        {
            [ReviewCaller.Anonymous] = fixture.Factory.CreateClient(),
            [ReviewCaller.Stranger] = stranger,
            [ReviewCaller.OwnerUser] = owner,
            [ReviewCaller.OtherModerator] = other,
            [ReviewCaller.Admin] = admin,
            [ReviewCaller.RevokedModerator] = revoked
        };
        return new ReviewCallers(clients, ownerName);
    }

    public static void DisposeAll(ReviewCallers callers)
    {
        foreach (HttpClient client in callers.Clients.Values)
        {
            client.Dispose();
        }
    }

    /// <summary>
    /// Runs steps in order. Denials are checked for unchanged side effects. The owner must come after the
    /// owner-user steps because promotion changes the role of the same account.
    /// </summary>
    public async Task RunStepsAsync(
        ReviewCallers callers,
        ReviewCase seed,
        ReviewStep[] steps,
        string endpoint,
        Func<HttpClient, ReviewCase, Task<HttpResponseMessage>> call,
        bool moderatorRoute)
    {
        foreach (ReviewStep step in steps)
        {
            if (step.Caller == ReviewCaller.OwnerModerator && !callers.Clients.ContainsKey(ReviewCaller.OwnerModerator))
            {
                callers.Clients[ReviewCaller.OwnerModerator] = await IntegrationTestAuth.PromoteToModeratorAsync(fixture.Factory, callers.OwnerName);
            }

            var cell = $"endpoint {endpoint}, caller {step.Caller}";
            ReviewCaseSnapshot before = await ReadSnapshotAsync(seed);
            using HttpResponseMessage response = await call(callers.Clients[step.Caller], seed);
            var body = await response.Content.ReadAsStringAsync();
            response.StatusCode.Should().Be(step.Expected, cell + " status: " + body);
            if (step.ExpectedCode is not null)
            {
                body.Should().Contain(step.ExpectedCode, cell + " problem code");
            }

            var deniedOrRejected = (int)response.StatusCode is >= 400;
            if (deniedOrRejected)
            {
                ReviewCaseSnapshot expected = before with { AuditRows = before.AuditRows + step.AuditDelta };
                (await ReadSnapshotAsync(seed)).Should().BeEquivalentTo(expected, cell + " must leave no side effects beyond the expected audit rows");
            }

            if (moderatorRoute && step.Caller is ReviewCaller.OwnerModerator or ReviewCaller.OtherModerator)
            {
                // Handler-reached responses carry the no-store header; authorization-filter responses are not checked.
                response.Headers.CacheControl.Should().NotBeNull(cell + " Cache-Control header");
                response.Headers.CacheControl!.NoStore.Should().BeTrue(cell + " no-store");
            }
        }
    }

    /// <summary>Seeds one SUBMITTED case with a READY version and an admitted report.</summary>
    public async Task<ReviewCase> SeedCaseAsync(string ownerName, bool partialCoverage = false, bool withFinding = false)
    {
        var token = Guid.NewGuid().ToString("N");
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User owner = await db.Users.SingleAsync(u => u.Username == ownerName);
        var category = new Category { Id = Guid.NewGuid(), Name = "Review access", Slug = "review-access-" + token, CreatedAt = DateTimeOffset.UtcNow };
        var asset = new Asset { Id = Guid.NewGuid(), AuthorId = owner.Id, CategoryId = category.Id, Title = "Draft " + token, Description = "", Price = 1m, CreatedAt = DateTimeOffset.UtcNow };
        AssetVersion version = new()
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            VersionNumber = 1,
            IsCurrent = true,
            StorageKey = "review-access/" + token + "/source.bin",
            FileName = "source.zip",
            ContentLength = 1,
            ContentSha256 = new string('f', 64),
            ReleaseNotes = "Fixture",
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL).TemplateVersion,
            LicenseDisplayName = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL).DisplayName,
            LicenseTerms = AssetLicenseCatalog.Get(AssetLicenseCode.PERSONAL).TermsPlainText,
            ProcessingStatus = AssetVersionProcessingStatus.READY,
            ProcessingUpdatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Categories.Add(category);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        ModerationSubmission submission = ModerationDecisionFixture.AddReviewableSubmission(db, asset, version, owner);
        await db.SaveChangesAsync();
        CodeAnalysisCoverageLevel coverage = partialCoverage ? CodeAnalysisCoverageLevel.PARTIAL : CodeAnalysisCoverageLevel.FULL;
        Guid reportId = await ModerationDecisionFixture.PrepareDecisionReadySubmission(
            db, submission, "Review access title " + token, null, coverage, partialCoverage ? 1 : 0);
        Guid findingId = Guid.Empty;
        if (withFinding)
        {
            findingId = Guid.NewGuid();
            db.CodeAnalysisReportFindings.Add(new CodeAnalysisReportFinding
            {
                Id = findingId,
                ReportId = reportId,
                FindingKey = "finding-" + token,
                FindingType = "POTENTIAL_MATCH",
                OwnScopePath = "src/review-access.cs",
                OwnExcerpt = "review access excerpt",
                ForeignSensitivity = CodeAnalysisFindingSensitivity.PUBLIC,
                ReasonText = "Review access fixture finding.",
                RequestedAction = "review",
                RetrievalEvidenceJson = "{\"material\":true}",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            await db.CodeAnalysisReportHeaders.Where(h => h.Id == reportId)
                .ExecuteUpdateAsync(update => update.SetProperty(h => h.FindingCount, 1));
        }

        return new ReviewCase(asset.Id, version.Id, submission.Id, reportId, submission.WorkspaceId, findingId, ownerName);
    }

    /// <summary>Claims the case through the real claim route for the live moderator who is not the author.</summary>
    public static async Task ClaimForOtherAsync(ReviewCallers callers, ReviewCase seed)
    {
        using HttpResponseMessage claimed = await ModerationDecisionFixture.PostClaimAsync(callers.Clients[ReviewCaller.OtherModerator], seed.SubmissionId, Guid.NewGuid(), 1);
        claimed.StatusCode.Should().Be(HttpStatusCode.OK, await claimed.Content.ReadAsStringAsync());
    }

    /// <summary>Owner files a coverage request for the seeded report through the seller route.</summary>
    public static async Task<CoverageReviewSaved> CreateCoverageRequestAsync(ReviewCallers callers, ReviewCase seed)
    {
        HttpClient owner = callers.Clients[ReviewCaller.OwnerUser];
        using HttpResponseMessage response = await owner.PostAsJsonAsync(
            RouteUri(ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_COVERAGE_REVIEWS, ("{id:guid}", seed.AssetId), ("{versionId:guid}", seed.VersionId)),
            new CoverageReviewCreateRequest(Guid.NewGuid(), seed.WorkspaceId, seed.ReportId, 1));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<CoverageReviewSaved>())!;
    }

    public async Task<object> ReadBasisAsync(ReviewCase seed) =>
        await ModerationDecisionFixture.ReadDecisionBasis(fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(), seed.SubmissionId, seed.ReportId);

    public async Task<ReviewCaseSnapshot> ReadSnapshotAsync(ReviewCase seed)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == seed.SubmissionId);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == seed.WorkspaceId);
        // Audit and outbox counts are scoped to this case. Outbox rows are limited to the review-written types: the
        // dispatcher fans earlier lifecycle events out into notification and email rows asynchronously.
        List<Guid> requestIds = await db.CoverageReviewRequests.AsNoTracking().Where(r => r.AssetId == seed.AssetId).Select(r => r.Id).ToListAsync();
        var auditScopeKeys = new List<string> { seed.AssetId.ToString(), seed.VersionId.ToString(), seed.SubmissionId.ToString(), seed.ReportId.ToString() };
        auditScopeKeys.AddRange(requestIds.Select(id => id.ToString()));
        return new ReviewCaseSnapshot(
            row.State,
            row.ClaimHolderId,
            row.CaseRevision,
            workspace.WorkspaceRevision,
            await db.AssetSellerEvidenceRevisions.CountAsync(r => r.AssetId == seed.AssetId),
            await db.SellerResponses.CountAsync(r => r.AssetId == seed.AssetId),
            await db.ModerationDecisionRecords.CountAsync(d => d.AssetId == seed.AssetId),
            await db.PublicationSnapshots.CountAsync(p => p.AssetId == seed.AssetId),
            await db.ModerationSubmissions.CountAsync(s => s.AssetId == seed.AssetId),
            await db.CoverageReviewRequests.CountAsync(r => r.AssetId == seed.AssetId),
            await db.CoverageReviewRequests.CountAsync(r => r.AssetId == seed.AssetId && r.IsOpen),
            await db.CoverageReviewRequests.Where(r => r.AssetId == seed.AssetId).SumAsync(r => (long?)r.Revision) ?? 0,
            await db.CoverageAssessments.CountAsync(a => db.CoverageReviewRequests.Any(r => r.Id == a.RequestId && r.AssetId == seed.AssetId)),
            await db.OutboxMessages.CountAsync(o => (o.Type == OutboxMessageTypes.MODERATION_DECISION || o.Type == OutboxMessageTypes.MODERATION_LIFECYCLE)
                && (o.Payload.Contains(seed.AssetId.ToString()) || o.Payload.Contains(seed.SubmissionId.ToString()))),
            await db.AuditLogs.CountAsync(a => a.ResourceId != null && auditScopeKeys.Contains(a.ResourceId)));
    }

    /// <summary>Current workspace and seller-evidence revisions, read fresh so write steps carry valid expectations.</summary>
    public async Task<ReviewSellerRevisions> ReadSellerRevisionsAsync(ReviewCase seed)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(w => w.Id == seed.WorkspaceId);
        return new ReviewSellerRevisions(workspace.WorkspaceRevision, workspace.SellerEvidenceHeadRevision);
    }

    /// <summary>Selected analysis run of the seeded report, for retry expectations.</summary>
    public async Task<CodeAnalysisRun> ReadRunAsync(ReviewCase seed)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        CodeAnalysisReportHeader report = await db.CodeAnalysisReportHeaders.AsNoTracking().SingleAsync(h => h.Id == seed.ReportId);
        return await db.CodeAnalysisRuns.AsNoTracking().SingleAsync(r => r.Id == report.AnalysisRunId);
    }

    /// <summary>Submission ids on every moderator queue page the caller can see. Follows the cursor until the queue ends.</summary>
    public static async Task<HashSet<string>> ReadAllQueueSubmissionIdsAsync(HttpClient client)
    {
        var ids = new HashSet<string>();
        string? cursor = null;
        for (var pageNumber = 1; pageNumber <= MAX_PAGES; pageNumber++)
        {
            var query = "?pageSize=" + QUEUE_PAGE_SIZE + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
            using HttpResponseMessage response = await client.GetAsync(RouteUri(ApiRoutes.Moderation.QUEUE + query));
            response.StatusCode.Should().Be(HttpStatusCode.OK, "queue page " + pageNumber);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            foreach (JsonElement item in json.RootElement.GetProperty("items").EnumerateArray())
            {
                ids.Add(item.GetProperty("submissionId").GetGuid().ToString());
            }

            JsonElement next = json.RootElement.GetProperty("nextCursor");
            if (next.ValueKind != JsonValueKind.String)
            {
                return ids;
            }

            cursor = next.GetString();
        }

        throw new InvalidOperationException("Moderator queue did not end within " + MAX_PAGES + " pages.");
    }

    /// <summary>Coverage request ids on every list page the caller can see. Pages are read until TotalPages.</summary>
    public static async Task<HashSet<string>> ReadAllCoverageRequestIdsAsync(HttpClient client)
    {
        var ids = new HashSet<string>();
        for (var pageNumber = 1; pageNumber <= MAX_PAGES; pageNumber++)
        {
            using HttpResponseMessage response = await client.GetAsync(RouteUri(ApiRoutes.Moderation.COVERAGE_REQUESTS + "?page=" + pageNumber));
            response.StatusCode.Should().Be(HttpStatusCode.OK, "coverage request page " + pageNumber);
            CoverageQueuePage page = (await response.Content.ReadFromJsonAsync<CoverageQueuePage>(IntegrationTestAuth.JsonOptions))!;
            foreach (CoverageQueueItem item in page.Items)
            {
                ids.Add(item.RequestId.ToString());
            }

            if (pageNumber >= page.TotalPages)
            {
                return ids;
            }
        }

        throw new InvalidOperationException("Coverage request list did not end within " + MAX_PAGES + " pages.");
    }

    /// <summary>Builds a relative request URI, filling each route token with its value.</summary>
    public static Uri RouteUri(string route, params (string Token, object Value)[] tokens)
    {
        var path = route;
        for (var i = 0; i < tokens.Length; i++)
        {
            path = path.Replace(tokens[i].Token, tokens[i].Value.ToString(), StringComparison.Ordinal);
        }

        return new Uri(path.StartsWith('/') ? path : "/" + path, UriKind.Relative);
    }

    private async Task SetRoleAsync(string username, string role)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        User user = await db.Users.SingleAsync(u => u.Username == username);
        user.Role = role;
        user.RoleRevision++;
        await db.SaveChangesAsync();
    }
}
