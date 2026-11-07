using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class PrivacyReviewCanaryIntegrationTests(IntegrationTestFixture fixture)
{
    private const string PRIVATE_SOURCE = "PRIVATE-SOURCE-CANARY-9A6F";
    private const string PRIVATE_RATIONALE = "PRIVATE-RATIONALE-CANARY-51D2";
    private const string SELLER_NOTE = "SELLER-NOTE-CANARY-7C14";
    private const string OTHER_SELLER_SOURCE = "OTHER-SELLER-CANARY-2E80";
    private const int QUEUE_PAGE_SIZE = 1;
    private const int MAX_QUEUE_PAGES = 200;

    private readonly ReviewAccessMatrix _matrix = new(fixture);

    [Fact]
    public async Task ReviewFlow_WhenPrivateCanariesCrossSellers_ShouldExposeOnlyAuthorizedSurfaces()
    {
        ReviewCallers callers = await _matrix.CreateCallersAsync();
        (HttpClient otherOwner, var otherOwnerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        try
        {
            ReviewCase olderEligible = await _matrix.SeedCaseAsync(callers.OwnerName, withFinding: true);
            await SetSubmissionCreatedAtAsync(olderEligible, DateTimeOffset.UtcNow.AddDays(-1));
            ReviewCase owned = await _matrix.SeedCaseAsync(callers.OwnerName, withFinding: true);
            ReviewCase other = await _matrix.SeedCaseAsync(otherOwnerName, withFinding: true);
            await SetFindingTextAsync(owned, PRIVATE_SOURCE, material: false);
            await SetFindingTextAsync(other, OTHER_SELLER_SOURCE, material: true);

            ReviewSellerRevisions sellerRevisions = await _matrix.ReadSellerRevisionsAsync(other);
            using (HttpResponseMessage saved = await otherOwner.PostAsJsonAsync(
                ReviewAccessMatrix.RouteUri(ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_RESPONSES,
                    ("{id:guid}", other.AssetId), ("{versionId:guid}", other.VersionId)),
                new SellerResponseRequest(other.WorkspaceId, other.ReportId, other.FindingId,
                    sellerRevisions.WorkspaceRevision, sellerRevisions.EvidenceRevision, Guid.NewGuid(),
                    SellerResponseType.INDEPENDENT_IMPLEMENTATION, SELLER_NOTE)))
            {
                saved.StatusCode.Should().Be(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
            }

            await ReviewAccessMatrix.ClaimForOtherAsync(callers, owned);
            HttpClient moderator = callers.Clients[ReviewCaller.OtherModerator];
            var queuePath = "/" + ApiRoutes.Moderation.QUEUE;
            string? cursor = null;
            var queueEnded = false;
            var olderEligiblePage = 0;
            var ownedPage = 0;
            for (var pageNumber = 1; pageNumber <= MAX_QUEUE_PAGES; pageNumber++)
            {
                var query = "?pageSize=" + QUEUE_PAGE_SIZE
                    + (cursor is null ? string.Empty : "&cursor=" + Uri.EscapeDataString(cursor));
                using HttpResponseMessage queueResponse = await moderator.GetAsync(queuePath + query);
                queueResponse.StatusCode.Should().Be(HttpStatusCode.OK, "queue page " + pageNumber);
                var queueBody = await queueResponse.Content.ReadAsStringAsync();
                AssertDoesNotContainPrivateCanaries(queueBody, includeSellerNote: true, because: "queue page " + pageNumber);

                using var queuePage = JsonDocument.Parse(queueBody);
                JsonElement items = queuePage.RootElement.GetProperty("items");
                items.GetArrayLength().Should().Be(QUEUE_PAGE_SIZE, "the seeded eligible cases must span multiple queue pages");
                foreach (JsonElement item in items.EnumerateArray())
                {
                    Guid submissionId = item.GetProperty("submissionId").GetGuid();
                    if (submissionId == olderEligible.SubmissionId)
                    {
                        olderEligiblePage = pageNumber;
                    }

                    if (submissionId == owned.SubmissionId)
                    {
                        ownedPage = pageNumber;
                    }
                }

                JsonElement nextCursor = queuePage.RootElement.GetProperty("nextCursor");
                if (nextCursor.ValueKind is not JsonValueKind.String)
                {
                    queueEnded = true;
                    break;
                }

                cursor = nextCursor.GetString();
            }

            queueEnded.Should().BeTrue("queue cursor traversal must end within " + MAX_QUEUE_PAGES + " pages");
            olderEligiblePage.Should().BeGreaterThan(0, "the explicit older eligible case must precede the owned case");
            ownedPage.Should().BeGreaterThan(1, "the owned case must be beyond page one");
            olderEligiblePage.Should().BeLessThan(ownedPage);

            using HttpResponseMessage caseResponse = await moderator.GetAsync(ReviewAccessMatrix.RouteUri(
                ApiRoutes.Moderation.SUBMISSION_BY_ID, ("{id:guid}", owned.SubmissionId)));
            caseResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            caseResponse.Headers.CacheControl.Should().Match<System.Net.Http.Headers.CacheControlHeaderValue>(value => value.Private && value.NoStore);
            var moderatorSummary = await caseResponse.Content.ReadAsStringAsync();
            AssertDoesNotContainPrivateCanaries(moderatorSummary, includeSellerNote: true);

            var evidencePath = ApiRoutes.Moderation.CASE_EVIDENCE
                .Replace("{id:guid}", owned.SubmissionId.ToString(), StringComparison.Ordinal)
                .Replace("{kind}", "OWN_EXCERPT", StringComparison.Ordinal)
                + "?findingId=" + Uri.EscapeDataString(owned.FindingId.ToString());
            using HttpResponseMessage evidenceResponse = await moderator.GetAsync(evidencePath);
            evidenceResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            evidenceResponse.Headers.CacheControl.Should().Match<System.Net.Http.Headers.CacheControlHeaderValue>(value => value.Private && value.NoStore);
            RestrictedEvidenceDto evidence = (await evidenceResponse.Content.ReadFromJsonAsync<RestrictedEvidenceDto>())!;
            evidence.Content.Should().Be(PRIVATE_SOURCE);

            using HttpResponseMessage ownerResponses = await otherOwner.GetAsync(ReviewAccessMatrix.RouteUri(
                ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_RESPONSES,
                ("{id:guid}", other.AssetId), ("{versionId:guid}", other.VersionId)));
            ownerResponses.StatusCode.Should().Be(HttpStatusCode.OK);
            ownerResponses.Headers.CacheControl.Should().Match<System.Net.Http.Headers.CacheControlHeaderValue>(value => value.Private && value.NoStore);
            var sellerJson = await ownerResponses.Content.ReadAsStringAsync();
            sellerJson.Should().Contain(SELLER_NOTE);
            AssertDoesNotContainPrivateCanaries(sellerJson, includeSellerNote: false);

            await using AsyncServiceScope sellerNoteScope = fixture.Factory.Services.CreateAsyncScope();
            ApplicationDbContext sellerNoteDb = sellerNoteScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            List<string?> sellerAuditMetadata = await sellerNoteDb.AuditLogs.AsNoTracking()
                .Where(row => row.ResourceId == other.VersionId.ToString())
                .Select(row => row.MetadataJson).ToListAsync();
            foreach (var metadata in sellerAuditMetadata)
            {
                metadata.Should().NotBeNull();
                AssertDoesNotContainPrivateCanaries(metadata!, includeSellerNote: true);
            }

            List<string> sellerHistory = await sellerNoteDb.ModerationSubmissionHistoryEntries.AsNoTracking()
                .Where(row => row.SubmissionId == other.SubmissionId).Select(row => row.Summary).ToListAsync();
            sellerHistory.Should().NotBeEmpty();
            foreach (var summary in sellerHistory)
            {
                AssertDoesNotContainPrivateCanaries(summary, includeSellerNote: true);
            }

            List<string> sellerOutboxPayloads = await sellerNoteDb.OutboxMessages.AsNoTracking()
                .Where(row => row.Payload.Contains(other.SubmissionId.ToString()) || row.Payload.Contains(other.AssetId.ToString())
                    || row.Payload.Contains(other.VersionId.ToString()))
                .Select(row => row.Payload).ToListAsync();
            foreach (var payload in sellerOutboxPayloads)
            {
                AssertDoesNotContainPrivateCanaries(payload, includeSellerNote: true);
            }

            List<string> sellerNotificationMetadata = await sellerNoteDb.UserNotifications.AsNoTracking()
                .Where(row => row.SourceOutboxMessageId != null && sellerNoteDb.OutboxMessages.Any(message => message.Id == row.SourceOutboxMessageId
                    && (message.Payload.Contains(other.SubmissionId.ToString()) || message.Payload.Contains(other.AssetId.ToString())
                        || message.Payload.Contains(other.VersionId.ToString()))))
                .Select(row => row.MetadataJson).ToListAsync();
            foreach (var metadata in sellerNotificationMetadata)
            {
                AssertDoesNotContainPrivateCanaries(metadata, includeSellerNote: true);
            }

            HttpClient owner = callers.Clients[ReviewCaller.OwnerUser];
            using HttpResponseMessage otherSellerDenied = await otherOwner.GetAsync(ReviewAccessMatrix.RouteUri(
                ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_RESPONSES,
                ("{id:guid}", owned.AssetId), ("{versionId:guid}", owned.VersionId)));
            otherSellerDenied.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await otherSellerDenied.Content.ReadAsStringAsync()).Should().NotContain(SELLER_NOTE).And.NotContain(PRIVATE_SOURCE);

            var basis = await _matrix.ReadBasisAsync(owned);
            var decision = new
            {
                operationId = Guid.NewGuid(),
                basis,
                outcome = "APPROVE",
                rationale = PRIVATE_RATIONALE,
                dispositions = new[] { new { findingId = owned.FindingId, disposition = "EXPLAINED", rationale = "Canary finding resolved." } },
                manualPlanCompletionReference = (string?)null
            };
            using (HttpResponseMessage decided = await ModerationDecisionFixture.PostDecisionAsync(moderator, owned.SubmissionId, decision))
            {
                decided.StatusCode.Should().Be(HttpStatusCode.OK, await decided.Content.ReadAsStringAsync());
            }

            using HttpClient publicClient = fixture.Factory.CreateClient();
            using HttpResponseMessage publicDetail = await publicClient.GetAsync(
                ReviewAccessMatrix.RouteUri(ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.ID, ("{id:guid}", owned.AssetId)));
            publicDetail.StatusCode.Should().Be(HttpStatusCode.OK);
            var publicJson = await publicDetail.Content.ReadAsStringAsync();
            AssertDoesNotContainPrivateCanaries(publicJson, includeSellerNote: true);

            using HttpResponseMessage sellerReview = await owner.GetAsync("/" + ApiRoutes.Users.BASE + "/"
                + ApiRoutes.Users.ME_ASSET_VERSION_REVIEW.Replace("{assetVersionId:guid}", owned.VersionId.ToString(), StringComparison.Ordinal));
            sellerReview.StatusCode.Should().Be(HttpStatusCode.OK);
            var sellerReviewJson = await sellerReview.Content.ReadAsStringAsync();
            AssertDoesNotContainPrivateCanaries(sellerReviewJson, includeSellerNote: true);

            await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ModerationDecisionRecord decisionRecord = await db.ModerationDecisionRecords.AsNoTracking().SingleAsync(row => row.SubmissionId == owned.SubmissionId);
            decisionRecord.Message.Should().Be(PRIVATE_RATIONALE);
            List<string> outboxPayloads = await db.OutboxMessages.AsNoTracking()
                .Where(row => row.Payload.Contains(owned.SubmissionId.ToString()) || row.Payload.Contains(owned.AssetId.ToString()))
                .Select(row => row.Payload).ToListAsync();
            outboxPayloads.Should().NotBeEmpty();
            foreach (var payload in outboxPayloads)
            {
                AssertDoesNotContainPrivateCanaries(payload, includeSellerNote: true);
            }

            List<string?> auditMetadata = await db.AuditLogs.AsNoTracking()
                .Where(row => row.ResourceId == owned.SubmissionId.ToString())
                .Select(row => row.MetadataJson).ToListAsync();
            auditMetadata.Should().NotBeEmpty();
            foreach (var metadata in auditMetadata)
            {
                metadata.Should().NotBeNull();
                AssertDoesNotContainPrivateCanaries(metadata!, includeSellerNote: true);
            }

            List<string> history = await db.ModerationSubmissionHistoryEntries.AsNoTracking()
                .Where(row => row.SubmissionId == owned.SubmissionId).Select(row => row.Summary).ToListAsync();
            history.Should().NotBeEmpty();
            foreach (var summary in history)
            {
                AssertDoesNotContainPrivateCanaries(summary, includeSellerNote: true);
            }

            List<string> notificationMetadata = await db.UserNotifications.AsNoTracking()
                .Where(row => row.SourceOutboxMessageId != null && db.OutboxMessages.Any(message => message.Id == row.SourceOutboxMessageId
                    && (message.Payload.Contains(owned.SubmissionId.ToString()) || message.Payload.Contains(owned.AssetId.ToString()))))
                .Select(row => row.MetadataJson).ToListAsync();
            foreach (var metadata in notificationMetadata)
            {
                AssertDoesNotContainPrivateCanaries(metadata, includeSellerNote: true);
            }
        }
        finally
        {
            otherOwner.Dispose();
            ReviewAccessMatrix.DisposeAll(callers);
        }
    }

    private async Task SetFindingTextAsync(ReviewCase seed, string text, bool material)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        CodeAnalysisReportFinding finding = await db.CodeAnalysisReportFindings.SingleAsync(row => row.Id == seed.FindingId);
        finding.OwnExcerpt = text;
        finding.RetrievalEvidenceJson = material ? "{\"material\":true}" : "{\"material\":false}";
        await db.SaveChangesAsync();
    }

    private async Task SetSubmissionCreatedAtAsync(ReviewCase seed, DateTimeOffset createdAt)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.ModerationSubmissions.Where(row => row.Id == seed.SubmissionId)
            .ExecuteUpdateAsync(update => update.SetProperty(row => row.CreatedAt, createdAt));
    }

    private static void AssertDoesNotContainPrivateCanaries(string value, bool includeSellerNote, string because = "Private canaries must remain on authorized surfaces.")
    {
        value.Should().NotContain(PRIVATE_SOURCE, because);
        value.Should().NotContain(PRIVATE_RATIONALE, because);
        value.Should().NotContain(OTHER_SELLER_SOURCE, because);
        if (includeSellerNote)
        {
            value.Should().NotContain(SELLER_NOTE, because);
        }
    }
}
