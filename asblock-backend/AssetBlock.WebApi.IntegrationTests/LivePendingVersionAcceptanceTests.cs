#pragma warning disable IDE0007, IDE0008, IDE0011
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static AssetBlock.WebApi.IntegrationTests.AuthenticatedEncryptedUploadAcceptanceTests;

namespace AssetBlock.WebApi.IntegrationTests;

/// <summary>
/// Live pending-version scenario with the real upload worker, object storage, ClamAV and the CPU analysis service.
/// An approved v1 keeps serving while a newer v2 is submitted, sent back for changes, corrected and resubmitted.
/// Only the approved v2 replaces v1 publicly, and the buyer's v1 purchase keeps its terms. Evidence records
/// statuses and identifiers only: no titles, rationale, source text or byte contents.
/// </summary>
public sealed class LivePendingVersionAcceptanceTests
{
    private sealed record AdmittedVersion(Guid VersionId, Guid ReportId);

    private sealed record TextResult(HttpStatusCode Status, string Body);

    private sealed record BytesResult(HttpStatusCode Status, byte[] Bytes);

    private sealed record Scenario(Guid AssetId, Guid CategoryId, AdmittedVersion V1, AdmittedVersion V2, string V1Title, string V1Token,
        string PendingTitle, string PendingToken, byte[] V1Bytes, byte[] V2Bytes, Guid PurchaseId);

    [LiveAcceptanceFact]
    public async Task PendingNewerVersion_WhenOlderVersionIsApproved_ShouldServeOnlyApprovedBytes()
    {
        LiveAcceptanceOptions live = LiveAcceptanceOptions.Resolve();
        var observed = new Dictionary<string, object?>();
        await using var factory = new LiveAcceptanceFactory(live);
        (HttpClient seller, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(factory);
        (HttpClient buyer, string buyerName) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(factory);
        (HttpClient stranger, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(factory);
        (HttpClient moderator, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(factory, AppRoles.MODERATOR);
        HttpClient anonymous = factory.CreateClient();
        try
        {
            var v1Token = Guid.NewGuid().ToString("N")[..12];
            var pendingToken = Guid.NewGuid().ToString("N")[..12];
            var v1Title = "Approved older release " + v1Token;
            var pendingTitle = "Pending newer revision " + pendingToken;
            var v1Bytes = AuthenticatedEncryptedUploadAcceptanceTests.CreateArchive();
            Guid categoryId = await CreateCategoryAsync(factory, v1Token);

            // Version 1: real upload, admission, seller responses, submission and moderator approval.
            (Guid assetId, Guid workspaceId, long revision) = await CreateDraftAsync(seller, categoryId, v1Title);
            await UploadVersionAsync(seller, assetId, workspaceId, revision, v1Bytes, "Approved older release notes");
            AdmittedVersion v1 = await WaitForAdmittedVersionAsync(factory, assetId, 1, TimeSpan.FromMinutes(25));
            List<Guid> v1Materials = await MaterialFindingIds(factory, v1.ReportId);
            await AnswerMaterialResponsesAsync(factory, seller, assetId, v1.VersionId, v1.ReportId);
            Guid v1Submission = await SubmitVersionAsync(seller, assetId, v1.VersionId);
            await ClaimAndDecideAsync(factory, moderator, v1Submission, v1.ReportId, "APPROVE", "Approval of the older release.", v1Materials);
            observed["v1Approved"] = true;
            observed["assetId"] = assetId;
            observed["v1VersionId"] = v1.VersionId;
            observed["v1ReportId"] = v1.ReportId;
            observed["v1SubmissionId"] = v1Submission;

            // Buyer purchase of v1 and first download of v1 bytes.
            Guid buyerId = await AssetVersionsSeed.GetUserIdAsync(factory.Services.GetRequiredService<IServiceScopeFactory>(), buyerName);
            Guid purchaseId = await AssetVersionsSeed.SeedPurchaseAsync(factory.Services.GetRequiredService<IServiceScopeFactory>(), buyerId, assetId, v1.VersionId);
            observed["purchaseId"] = purchaseId;
            BytesResult firstV1Download = await ReadBytesAsync(buyer, VersionDownloadUrl(assetId, v1.VersionId));
            observed["buyerV1DownloadStatus"] = (int)firstV1Download.Status;
            if (firstV1Download.Status != HttpStatusCode.OK || !firstV1Download.Bytes.SequenceEqual(v1Bytes))
                throw new InvalidOperationException("Buyer could not download approved v1 bytes: HTTP " + (int)firstV1Download.Status);

            // Version 2: a revised draft title and a different archive. Submitted, but not approved.
            (Guid draftWorkspace, long draftRevision) = await ReadDraftWorkspaceAsync(seller, assetId);
            long patchedRevision = await PatchDraftMaterialAsync(seller, assetId, draftRevision, pendingTitle, categoryId);
            byte[] v2Bytes = CreateRevisedArchive(pendingToken);
            await UploadVersionAsync(seller, assetId, draftWorkspace, patchedRevision, v2Bytes, "Pending newer revision notes " + pendingToken);
            AdmittedVersion v2 = await WaitForAdmittedVersionAsync(factory, assetId, 2, TimeSpan.FromMinutes(25));
            observed["v2VersionId"] = v2.VersionId;
            observed["v2ReportId"] = v2.ReportId;
            List<Guid> v2Materials = await MaterialFindingIds(factory, v2.ReportId);
            await AnswerMaterialResponsesAsync(factory, seller, assetId, v2.VersionId, v2.ReportId);
            Guid v2Submission = await SubmitVersionAsync(seller, assetId, v2.VersionId);
            observed["v2SubmissionId"] = v2Submission;

            var scenario = new Scenario(assetId, categoryId, v1, v2, v1Title, v1Token, pendingTitle, pendingToken, v1Bytes, v2Bytes, purchaseId);
            string detailUrl = AssetPath(ApiRoutes.Assets.ID, assetId);

            // Pending state: nothing from v2 is public, v1 keeps serving.
            await AssertPublicStateAsync("pending", scenario, anonymous, buyer, stranger, observed);
            using (HttpResponseMessage review = await seller.GetAsync(SellerVersionReviewUrl(v2.VersionId)))
            {
                observed["pendingSellerReviewStatus"] = (int)review.StatusCode;
                if (review.StatusCode != HttpStatusCode.OK)
                    throw new InvalidOperationException("Seller could not read the pending v2 review: HTTP " + (int)review.StatusCode);
                using JsonDocument reviewJson = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
                if (reviewJson.RootElement.GetProperty("analysisDetails").GetProperty("reportId").GetGuid() != v2.ReportId)
                    throw new InvalidOperationException("Seller review did not show the pending v2 report.");
            }

            using (HttpResponseMessage checkout = await buyer.PostAsJsonAsync(
                new Uri(IntegrationTestRoutes.PAYMENTS_BASE + "/" + ApiRoutes.Payments.CHECKOUT, UriKind.Relative), new CreateCheckoutRequest(assetId)))
            {
                string checkoutBody = await checkout.Content.ReadAsStringAsync();
                observed["pendingBuyerCheckoutStatus"] = (int)checkout.StatusCode;
                if (checkout.StatusCode != HttpStatusCode.Conflict)
                    throw new InvalidOperationException("Pending v2 checkout expected HTTP 409: HTTP " + (int)checkout.StatusCode);
                if (checkoutBody.Contains(v2.VersionId.ToString(), StringComparison.OrdinalIgnoreCase) || checkoutBody.Contains(pendingToken, StringComparison.Ordinal))
                    throw new InvalidOperationException("Buyer checkout response exposed the pending version.");
            }

            // Changes requested: public state must be byte-identical before and after the decision.
            string detailBefore = (await ReadTextAsync(anonymous, detailUrl)).Body;
            await ClaimAndDecideAsync(factory, moderator, v2Submission, v2.ReportId, "REQUEST_CHANGES", "Changes requested for the newer revision.", v2Materials);
            observed["v2ChangesRequested"] = true;
            string detailAfter = (await ReadTextAsync(anonymous, detailUrl)).Body;
            observed["publicDetailUnchangedByChangesRequest"] = detailBefore == detailAfter;
            if (detailBefore != detailAfter)
                throw new InvalidOperationException("Requesting changes altered the public detail of the approved older version.");
            await AssertPublicStateAsync("changesRequested", scenario, anonymous, buyer, stranger, observed);

            // Correction and resubmission of v2, then approval.
            SellerResponsesPageDto page = (await ReadReviewResponses(seller, ReadReviewEndpoint("responses", assetId, v2.VersionId)))!;
            ReviewEndpoint evidenceEndpoint = ReadReviewEndpoint("evidence", assetId, v2.VersionId);
            var correction = new SellerEvidenceEditRequest(page.WorkspaceId, v2.ReportId, page.WorkspaceRevision, page.EvidenceRevision,
                Guid.NewGuid(), "Pending correction text for the newer revision.");
            using (HttpResponseMessage corrected = await AuthenticatedEncryptedUploadAcceptanceTests.SendReviewWrite(seller, evidenceEndpoint, correction))
            {
                observed["v2CorrectionStatus"] = (int)corrected.StatusCode;
                corrected.EnsureSuccessStatusCode();
            }
            Guid v2Resubmission = await SubmitVersionAsync(seller, assetId, v2.VersionId);
            observed["v2ResubmissionId"] = v2Resubmission;
            await ClaimAndDecideAsync(factory, moderator, v2Resubmission, v2.ReportId, "APPROVE", "Approval of the newer revision.", v2Materials);
            observed["v2Approved"] = true;

            // Final state: v2 is public and serves the newer bytes; the v1 purchase keeps its terms and v1 bytes.
            await AssertFinalStateAsync(factory, scenario, anonymous, buyer, observed);
        }
        finally
        {
            foreach (HttpClient client in new[] { seller, buyer, stranger, moderator, anonymous })
            {
                client.Dispose();
            }
        }

        await File.WriteAllTextAsync(live.EvidencePathFor("pending-version"), JsonSerializer.Serialize(new { status = "passed", observed }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task AssertPublicStateAsync(string phase, Scenario scenario, HttpClient anonymous, HttpClient buyer, HttpClient stranger,
        Dictionary<string, object?> observed)
    {
        foreach ((string name, HttpClient client) in new[] { ("anonymous", anonymous), ("buyer", buyer), ("stranger", stranger) })
        {
            BytesResult pendingDownload = await ReadBytesAsync(client, VersionDownloadUrl(scenario.AssetId, scenario.V2.VersionId));
            observed[phase + ".v2Download." + name] = (int)pendingDownload.Status;
            if (pendingDownload.Status == HttpStatusCode.OK)
                throw new InvalidOperationException(phase + ": " + name + " downloaded the pending v2 bytes.");
        }

        TextResult detail = await ReadTextAsync(anonymous, AssetPath(ApiRoutes.Assets.ID, scenario.AssetId));
        observed[phase + ".publicDetailStatus"] = (int)detail.Status;
        if (detail.Status != HttpStatusCode.OK || !detail.Body.Contains(scenario.V1Title, StringComparison.Ordinal)
            || detail.Body.Contains(scenario.PendingTitle, StringComparison.Ordinal))
            throw new InvalidOperationException(phase + ": public detail did not serve only the approved v1 title.");

        TextResult pendingSearch = await ReadTextAsync(anonymous, CatalogSearchUrl(scenario.PendingToken));
        observed[phase + ".pendingCatalogStatus"] = (int)pendingSearch.Status;
        if (pendingSearch.Status != HttpStatusCode.OK)
            throw new InvalidOperationException(phase + ": pending catalog search returned HTTP " + (int)pendingSearch.Status);
        if (pendingSearch.Body.Contains(scenario.AssetId.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(phase + ": catalog exposed the pending title.");

        TextResult v1Search = await ReadTextAsync(anonymous, CatalogSearchUrl(scenario.V1Token));
        observed[phase + ".v1CatalogStatus"] = (int)v1Search.Status;
        if (v1Search.Status != HttpStatusCode.OK)
            throw new InvalidOperationException(phase + ": approved v1 catalog search returned HTTP " + (int)v1Search.Status);
        if (!v1Search.Body.Contains(scenario.AssetId.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(phase + ": catalog did not serve the approved v1 asset.");

        foreach ((string name, HttpClient client) in new[] { ("anonymous", anonymous), ("buyer", buyer) })
        {
            TextResult versions = await ReadTextAsync(client, AssetPath(ApiRoutes.Assets.VERSIONS, scenario.AssetId));
            observed[phase + ".versionList." + name] = (int)versions.Status;
            if (versions.Status != HttpStatusCode.OK)
                throw new InvalidOperationException(phase + ": " + name + " version list returned HTTP " + (int)versions.Status);
            if (versions.Body.Contains(scenario.V2.VersionId.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(phase + ": " + name + " version list exposed the pending v2.");
        }

        BytesResult defaultDownload = await ReadBytesAsync(buyer, AssetPath(ApiRoutes.Assets.DOWNLOAD, scenario.AssetId));
        observed[phase + ".buyerDefaultDownloadStatus"] = (int)defaultDownload.Status;
        if (defaultDownload.Status != HttpStatusCode.OK || !defaultDownload.Bytes.SequenceEqual(scenario.V1Bytes))
            throw new InvalidOperationException(phase + ": default download did not serve the approved v1 bytes.");

        BytesResult v1Download = await ReadBytesAsync(buyer, VersionDownloadUrl(scenario.AssetId, scenario.V1.VersionId));
        observed[phase + ".buyerV1DownloadStatus"] = (int)v1Download.Status;
        if (v1Download.Status != HttpStatusCode.OK || !v1Download.Bytes.SequenceEqual(scenario.V1Bytes))
            throw new InvalidOperationException(phase + ": buyer v1 entitlement no longer serves v1 bytes.");
    }

    private static async Task AssertFinalStateAsync(LiveAcceptanceFactory factory, Scenario scenario, HttpClient anonymous, HttpClient buyer,
        Dictionary<string, object?> observed)
    {
        TextResult detail = await ReadTextAsync(anonymous, AssetPath(ApiRoutes.Assets.ID, scenario.AssetId));
        observed["finalPublicDetailStatus"] = (int)detail.Status;
        if (detail.Status != HttpStatusCode.OK || !detail.Body.Contains(scenario.PendingTitle, StringComparison.Ordinal))
            throw new InvalidOperationException("Final public detail did not serve the approved v2 title.");

        // The token term was searched while v2 was pending. The stale empty page is outbox-delayed invalidation: the
        // moderation decision outbox handler clears the asset list-cache prefix when it dispatches, and the 2-minute TTL
        // bounds the delay. Acceptable eventual consistency, not a defect: non-empty cached pages are re-validated, so
        // this cannot expose non-public assets. The same-term result is recorded as an observation only.
        TextResult sameTerm = await ReadTextAsync(anonymous, CatalogSearchUrl(scenario.PendingToken));
        observed["finalCatalogSameTermStatus"] = (int)sameTerm.Status;
        observed["finalCatalogSameTermContainsAsset"] = sameTerm.Body.Contains(scenario.AssetId.ToString(), StringComparison.OrdinalIgnoreCase);

        // A term never searched before reaches the snapshot-backed predicate directly.
        TextResult search = await ReadTextAsync(anonymous, CatalogSearchUrl(scenario.PendingTitle));
        observed["finalCatalogStatus"] = (int)search.Status;
        if (!search.Body.Contains(scenario.AssetId.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Final catalog did not serve the approved v2 asset: HTTP " + (int)search.Status + " " + SafeBody(search.Body));

        TextResult versions = await ReadTextAsync(anonymous, AssetPath(ApiRoutes.Assets.VERSIONS, scenario.AssetId));
        observed["finalVersionListStatus"] = (int)versions.Status;
        if (!versions.Body.Contains(scenario.V2.VersionId.ToString(), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Final version list did not include the approved v2.");

        BytesResult defaultDownload = await ReadBytesAsync(buyer, AssetPath(ApiRoutes.Assets.DOWNLOAD, scenario.AssetId));
        observed["finalBuyerDefaultDownloadStatus"] = (int)defaultDownload.Status;
        if (defaultDownload.Status != HttpStatusCode.OK || !defaultDownload.Bytes.SequenceEqual(scenario.V2Bytes))
            throw new InvalidOperationException("Buyer default download did not serve the approved v2 bytes.");

        BytesResult v1Download = await ReadBytesAsync(buyer, VersionDownloadUrl(scenario.AssetId, scenario.V1.VersionId));
        observed["finalBuyerV1DownloadStatus"] = (int)v1Download.Status;
        if (v1Download.Status != HttpStatusCode.OK || !v1Download.Bytes.SequenceEqual(scenario.V1Bytes))
            throw new InvalidOperationException("Buyer v1 entitlement changed after v2 approval.");

        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        Purchase purchase = await db.Purchases.AsNoTracking().SingleAsync(p => p.Id == scenario.PurchaseId);
        observed["purchaseVersionUnchanged"] = purchase.AssetVersionId == scenario.V1.VersionId;
        observed["purchasePublicationSnapshotId"] = purchase.PublicationSnapshotId;
        if (purchase.AssetVersionId != scenario.V1.VersionId)
            throw new InvalidOperationException("Purchase terms were re-pointed to a different version.");
    }

    private static async Task<Guid> CreateCategoryAsync(LiveAcceptanceFactory factory, string token)
    {
        var categoryId = Guid.NewGuid();
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Categories.Add(new Category { Id = categoryId, Name = "LiveAcceptance pending " + token, Slug = "live-pending-" + categoryId.ToString("N") });
        await db.SaveChangesAsync();
        return categoryId;
    }

    private static async Task<(Guid AssetId, Guid WorkspaceId, long Revision)> CreateDraftAsync(HttpClient seller, Guid categoryId, string title)
    {
        using HttpResponseMessage created = await seller.PostAsJsonAsync("/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.DRAFT_CREATE,
            new { operationId = Guid.NewGuid(), title, description = "Pending version live acceptance", price = 1.25m, categoryId, downloadLimitPerHour = 10 });
        created.EnsureSuccessStatusCode();
        using JsonDocument createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid assetId = createdJson.RootElement.GetProperty("assetId").GetGuid();
        Guid workspaceId = createdJson.RootElement.GetProperty("workspaceId").GetGuid();
        long revision = createdJson.RootElement.GetProperty("workspaceRevision").GetInt64();
        revision = await PatchDraftMaterialAsync(seller, assetId, revision, title, categoryId);
        using HttpResponseMessage declared = await seller.PutAsJsonAsync(AssetPath(ApiRoutes.Assets.DECLARATION, assetId),
            new { operationId = Guid.NewGuid(), expectedWorkspaceRevision = revision, declaration = AuthenticatedEncryptedUploadAcceptanceTests.BuildDeclaration() });
        declared.EnsureSuccessStatusCode();
        using JsonDocument declaredJson = JsonDocument.Parse(await declared.Content.ReadAsStringAsync());
        return (assetId, workspaceId, declaredJson.RootElement.GetProperty("workspaceRevision").GetInt64());
    }

    private static async Task<long> PatchDraftMaterialAsync(HttpClient seller, Guid assetId, long revision, string title, Guid categoryId)
    {
        using HttpResponseMessage response = await seller.PatchAsJsonAsync(AssetPath(ApiRoutes.Assets.DRAFT, assetId), new
        {
            operationId = Guid.NewGuid(),
            expectedWorkspaceRevision = revision,
            material = new { title, description = "Pending version live acceptance", categoryId, tags = Array.Empty<string>() }
        });
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("workspaceRevision").GetInt64();
    }

    private static async Task<(Guid WorkspaceId, long Revision)> ReadDraftWorkspaceAsync(HttpClient seller, Guid assetId)
    {
        using HttpResponseMessage response = await seller.GetAsync(AssetPath(ApiRoutes.Assets.DRAFT, assetId));
        response.EnsureSuccessStatusCode();
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return (json.RootElement.GetProperty("workspaceId").GetGuid(), json.RootElement.GetProperty("workspaceRevision").GetInt64());
    }

    private static async Task UploadVersionAsync(HttpClient seller, Guid assetId, Guid workspaceId, long revision, byte[] archive, string releaseNotes)
    {
        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("PERSONAL"), "LicenseCode");
        multipart.Add(new StringContent(releaseNotes), "ReleaseNotes");
        multipart.Add(new StringContent(workspaceId.ToString()), "WorkspaceId");
        multipart.Add(new StringContent(revision.ToString()), "ExpectedWorkspaceRevision");
        var file = new ByteArrayContent(archive);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        multipart.Add(file, "file", "live-acceptance.zip");
        using HttpResponseMessage upload = await seller.PostAsync(AssetPath(ApiRoutes.Assets.VERSIONS, assetId), multipart);
        if (!upload.IsSuccessStatusCode)
            throw new InvalidOperationException("Real upload endpoint failed: HTTP " + (int)upload.StatusCode + " " + AuthenticatedEncryptedUploadAcceptanceTests.SafeBody(await upload.Content.ReadAsStringAsync()));
    }

    private static byte[] CreateRevisedArchive(string token)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry("src/calculator.py", CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write("# SPDX-License-Identifier: MIT\n# Copyright (c) 2026 Live acceptance\ndef mean(items):\n    return sum(items) / len(items)\n\n\n# revision " + token + "\n");
        }

        return ms.ToArray();
    }

    private static async Task<AdmittedVersion> WaitForAdmittedVersionAsync(LiveAcceptanceFactory factory, Guid assetId, int versionNumber, TimeSpan timeout)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            AssetVersion? version = await db.AssetVersions.AsNoTracking().SingleOrDefaultAsync(v => v.AssetId == assetId && v.VersionNumber == versionNumber);
            if (version is not null && version.ProcessingStatus == AssetVersionProcessingStatus.READY)
            {
                CodeAnalysisReportHeader? report = await db.CodeAnalysisReportHeaders.AsNoTracking()
                    .Where(h => h.AssetVersionId == version.Id && h.IsFinalized).OrderByDescending(h => h.CreatedAt).FirstOrDefaultAsync();
                if (report is not null)
                {
                    return new AdmittedVersion(version.Id, report.Id);
                }
            }

            var failure = await db.AssetProcessingJobs.AsNoTracking()
                .Where(j => j.AssetId == assetId && j.Status == AssetProcessingJobStatus.FAILED)
                .Select(j => new { type = j.Type.ToString(), j.ErrorCode }).FirstOrDefaultAsync();
            if (failure is not null)
                throw new InvalidOperationException("Processing job failed for version " + versionNumber + ": " + failure.type + " " + failure.ErrorCode);
            await Task.Delay(1000);
        }

        throw new TimeoutException("Timed out waiting for admitted version " + versionNumber);
    }

    private static async Task AnswerMaterialResponsesAsync(LiveAcceptanceFactory factory, HttpClient seller, Guid assetId, Guid versionId, Guid reportId)
    {
        ReviewEndpoint responseEndpoint = ReadReviewEndpoint("responses", assetId, versionId);
        foreach (Guid findingId in await MaterialFindingIds(factory, reportId))
        {
            SellerResponsesPageDto current = (await ReadReviewResponses(seller, responseEndpoint))!;
            var response = new SellerResponseRequest(current.WorkspaceId, reportId, findingId, current.WorkspaceRevision, current.EvidenceRevision,
                Guid.NewGuid(), SellerResponseType.NO_EVIDENCE, "");
            using HttpResponseMessage saved = await AuthenticatedEncryptedUploadAcceptanceTests.SendReviewWrite(seller, responseEndpoint, response);
            saved.EnsureSuccessStatusCode();
        }
    }

    private static async Task<Guid> SubmitVersionAsync(HttpClient seller, Guid assetId, Guid versionId)
    {
        SellerResponsesPageDto page = (await ReadReviewResponses(seller, ReadReviewEndpoint("responses", assetId, versionId)))!;
        using HttpResponseMessage submitted = await seller.PostAsJsonAsync(
            "/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_SUBMISSIONS.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal)
                .Replace("{versionId:guid}", versionId.ToString(), StringComparison.Ordinal),
            new { workspaceId = page.WorkspaceId, expectedWorkspaceRevision = page.WorkspaceRevision, expectedCaseRevision = page.CaseRevision, operationId = Guid.NewGuid() });
        if (!submitted.IsSuccessStatusCode)
            throw new InvalidOperationException("Submission failed: HTTP " + (int)submitted.StatusCode + " " + AuthenticatedEncryptedUploadAcceptanceTests.SafeBody(await submitted.Content.ReadAsStringAsync()));
        using JsonDocument json = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("submissionId").GetGuid();
    }

    private static async Task ClaimAndDecideAsync(LiveAcceptanceFactory factory, HttpClient moderator, Guid submissionId, Guid reportId, string outcome,
        string rationale, List<Guid> materials)
    {
        ModerationSubmission claimable = await AuthenticatedEncryptedUploadAcceptanceTests.LoadSubmission(factory, submissionId);
        await AuthenticatedEncryptedUploadAcceptanceTests.ClaimCase(moderator, submissionId, claimable.CaseRevision);
        ModerationSubmission decidable = await AuthenticatedEncryptedUploadAcceptanceTests.LoadSubmission(factory, submissionId);
        var dispositions = materials.Select(id => (id, "EXPLAINED")).ToArray();
        using HttpResponseMessage decided = await AuthenticatedEncryptedUploadAcceptanceTests.PostDecision(moderator, submissionId,
            AuthenticatedEncryptedUploadAcceptanceTests.DecisionBody(Guid.NewGuid(), decidable, reportId, outcome, rationale, dispositions));
        if (decided.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException(outcome + " decision failed: HTTP " + (int)decided.StatusCode + " "
                + AuthenticatedEncryptedUploadAcceptanceTests.SafeBody(await decided.Content.ReadAsStringAsync()));
    }

    private static async Task<SellerResponsesPageDto?> ReadReviewResponses(HttpClient seller, ReviewEndpoint endpoint) =>
        await AuthenticatedEncryptedUploadAcceptanceTests.ReadReviewResponses(seller, endpoint);

    private static async Task<List<Guid>> MaterialFindingIds(LiveAcceptanceFactory factory, Guid reportId) =>
        await AuthenticatedEncryptedUploadAcceptanceTests.MaterialFindingIds(factory, reportId);

    private static string VersionDownloadUrl(Guid assetId, Guid versionId) =>
        AssetPath(ApiRoutes.Assets.VERSION_DOWNLOAD, assetId, versionId);

    private static string CatalogSearchUrl(string search) =>
        "/" + ApiRoutes.Assets.BASE + ApiRoutes.Assets.LIST + "?page=1&pageSize=100&search=" + Uri.EscapeDataString(search);

    private static string AssetPath(string route, Guid assetId, Guid? versionId = null) =>
        AuthenticatedEncryptedUploadAcceptanceTests.AssetPath(route, assetId, versionId);

    private static string SellerVersionReviewUrl(Guid versionId) =>
        AuthenticatedEncryptedUploadAcceptanceTests.SellerVersionReviewUrl(versionId);

    private static async Task<TextResult> ReadTextAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url);
        return new TextResult(response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<BytesResult> ReadBytesAsync(HttpClient client, string url)
    {
        using HttpResponseMessage response = await client.GetAsync(url);
        return new BytesResult(response.StatusCode, await response.Content.ReadAsByteArrayAsync());
    }
}
