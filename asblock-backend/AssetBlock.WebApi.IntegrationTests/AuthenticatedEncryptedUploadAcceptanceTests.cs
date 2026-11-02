using AssetBlock.Domain.Core.Dto.Moderation;
using System.IO.Compression;
using System.Net;
#pragma warning disable IDE0007, IDE0008, IDE0011
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using AssetBlock.WebApi.IntegrationTests.Support.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace AssetBlock.WebApi.IntegrationTests;

public sealed class AuthenticatedEncryptedUploadAcceptanceTests
{
    [LiveAcceptanceFact]
    public async Task UploadWorkerPersistsAdmittedReportAndDeclarationReuse()
    {
        var live = LiveAcceptanceOptions.Resolve();
        await RunLiveScenario(true);
        JsonElement upheld = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(live.EvidencePath));
        await RunLiveScenario(false);
        JsonElement approved = JsonSerializer.Deserialize<JsonElement>(await File.ReadAllTextAsync(live.EvidencePath));
        await File.WriteAllTextAsync(live.EvidencePath, JsonSerializer.Serialize(new { status = "passed", upheld, approved }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task RunLiveScenario(bool uphold)
    {
        var live = LiveAcceptanceOptions.Resolve();
        var evidencePath = live.EvidencePath;
        var details = new Dictionary<string, object?>();
        try
        {
            await using var factory = new LiveAcceptanceFactory(live);
            (HttpClient scopedClient, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(factory);
            using (scopedClient)
            {
                var categoryId = Guid.NewGuid();
                var approvalTag = "live-" + Guid.NewGuid().ToString("N")[..12];
                await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.Database.CanConnectAsync();
                    db.Categories.Add(new Category { Id = categoryId, Name = "LiveAcceptance Acceptance", Slug = "live-acceptance-" + categoryId.ToString("N") });
                    db.Tags.Add(new Tag { Id = Guid.NewGuid(), Name = approvalTag });
                    await db.SaveChangesAsync();
                }

                using HttpResponseMessage created = await scopedClient.PostAsJsonAsync("/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.DRAFT_CREATE,
                    new
                    {
                        operationId = Guid.NewGuid(),
                        title = "Live encrypted asset upload",
                        description = "Task-owned isolated acceptance archive",
                        price = 1.25m,
                        categoryId,
                        downloadLimitPerHour = 10
                    });
                created.EnsureSuccessStatusCode();
                using var createdJson = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                Guid assetId = createdJson.RootElement.GetProperty("assetId").GetGuid();
                Guid workspaceId = createdJson.RootElement.GetProperty("workspaceId").GetGuid();
                var revision = createdJson.RootElement.GetProperty("workspaceRevision").GetInt64();
                details["assetId"] = assetId;

                using (HttpResponseMessage material = await scopedClient.PatchAsJsonAsync(AssetPath(ApiRoutes.Assets.DRAFT, assetId), new
                {
                    operationId = Guid.NewGuid(),
                    expectedWorkspaceRevision = revision,
                    material = new { title = "Live encrypted asset upload", description = "Task-owned isolated acceptance archive", categoryId, tags = new[] { approvalTag } }
                }))
                {
                    material.EnsureSuccessStatusCode();
                    using JsonDocument materialJson = JsonDocument.Parse(await material.Content.ReadAsStringAsync());
                    revision = materialJson.RootElement.GetProperty("workspaceRevision").GetInt64();
                }

                var declaration = BuildDeclaration();
                using HttpResponseMessage saved = await scopedClient.PutAsJsonAsync(
                    AssetPath(ApiRoutes.Assets.DECLARATION, assetId),
                    new { operationId = Guid.NewGuid(), expectedWorkspaceRevision = revision, declaration });
                saved.EnsureSuccessStatusCode();
                using var savedJson = JsonDocument.Parse(await saved.Content.ReadAsStringAsync());
                var uploadRevision = savedJson.RootElement.GetProperty("workspaceRevision").GetInt64();

                var archive = CreateArchive();
                using var multipart = new MultipartFormDataContent();
                multipart.Add(new StringContent("PERSONAL"), "LicenseCode");
                multipart.Add(new StringContent("Initial review candidate"), "ReleaseNotes");
                multipart.Add(new StringContent(workspaceId.ToString()), "WorkspaceId");
                multipart.Add(new StringContent(uploadRevision.ToString()), "ExpectedWorkspaceRevision");
                var file = new ByteArrayContent(archive);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                multipart.Add(file, "file", "live-acceptance.zip");

                using HttpResponseMessage upload = await scopedClient.PostAsync(AssetPath(ApiRoutes.Assets.VERSIONS, assetId), multipart);
                var uploadBody = await upload.Content.ReadAsStringAsync();
                details["uploadStatus"] = (int)upload.StatusCode;
                if (!upload.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Real upload endpoint failed: HTTP " + (int)upload.StatusCode + " " + SafeBody(uploadBody));
                }

                var first = await WaitForAdmittedReport(factory, assetId, 1, TimeSpan.FromMinutes(25));
                details["first"] = first;
                JsonElement firstJson = JsonSerializer.SerializeToElement(first);
                JsonElement[] firstStages = firstJson.GetProperty("stages").EnumerateArray().ToArray();
                if (firstStages.Length != 8 || firstStages.Any(x => x.GetProperty("outcome").GetString() != (x.GetProperty("Stage").GetString() == "VERIFIER" ? "NOT_APPLICABLE" : "SUCCEEDED")))
                {
                    throw new InvalidOperationException("Initial report did not persist all eight policy-valid stages.");
                }

                if (firstJson.GetProperty("artifacts").GetArrayLength() != 3)
                {
                    throw new InvalidOperationException("Initial admitted report did not persist all three reusable artifacts.");
                }

                if (firstJson.GetProperty("reports").EnumerateArray().Any(x => !x.GetProperty("ReviewEligible").GetBoolean())
                    || firstJson.GetProperty("admissions").EnumerateArray().Any(x => !x.GetProperty("ReviewEligible").GetBoolean()))
                {
                    throw new InvalidOperationException("Initial report did not persist server-issued review eligibility.");
                }

                if (firstStages.Single(x => x.GetProperty("Stage").GetString() == "VERIFIER")
                        .GetProperty("tools").GetProperty("mode").GetString() != "DISABLED_BY_EVALUATION")
                {
                    throw new InvalidOperationException("Initial report did not record the evaluation-disabled verifier mode.");
                }

                Guid firstRunId = firstJson.GetProperty("runs").EnumerateArray().Last().GetProperty("Id").GetGuid();
                Guid assetVersionId = firstJson.GetProperty("assetVersionId").GetGuid();
                Guid admittedReportId;
                await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    admittedReportId = await db.CodeAnalysisReportHeaders.AsNoTracking()
                        .Where(header => header.AnalysisRunId == firstRunId && header.IsFinalized)
                        .Select(header => header.Id).SingleAsync();
                }

                using HttpResponseMessage reviewResponse = await scopedClient.GetAsync(
                    SellerVersionReviewUrl(assetVersionId));
                reviewResponse.EnsureSuccessStatusCode();
                using var ownerReview = JsonDocument.Parse(await reviewResponse.Content.ReadAsStringAsync());
                JsonElement summary = ownerReview.RootElement.GetProperty("analysisDetails");
                if (summary.GetProperty("reportId").GetGuid() != admittedReportId
                    || !summary.GetProperty("reportIsCurrent").GetBoolean()
                    || !summary.GetProperty("reviewEligible").GetBoolean())
                {
                    throw new InvalidOperationException("Owner analysis summary did not expose server-issued review eligibility.");
                }

                using HttpResponseMessage findingsResponse = await scopedClient.GetAsync(
                    "/" + ApiRoutes.Users.BASE + "/" + ApiRoutes.Users.ME_ASSET_VERSION_ANALYSIS_FINDINGS.Replace("{assetVersionId:guid}", assetVersionId.ToString(), StringComparison.Ordinal).Replace("{reportId:guid}", admittedReportId.ToString(), StringComparison.Ordinal) + "?page=1");
                findingsResponse.EnsureSuccessStatusCode();
                using var ownerFindings = JsonDocument.Parse(await findingsResponse.Content.ReadAsStringAsync());
                if (ownerFindings.RootElement.GetProperty("reportId").GetGuid() != admittedReportId
                    || ownerFindings.RootElement.GetProperty("items").GetArrayLength() > 50)
                {
                    throw new InvalidOperationException("Owner findings page did not return the admitted report safely.");
                }

                if (ownerFindings.RootElement.ToString().Contains("foreignSourceId", StringComparison.OrdinalIgnoreCase)
                    || ownerFindings.RootElement.ToString().Contains("tokenScore", StringComparison.OrdinalIgnoreCase)
                    || ownerFindings.RootElement.ToString().Contains("semanticScore", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Owner findings exposed private comparison metadata.");
                }

                details["workspaceId"] = workspaceId;

                var firstSelectionGeneration = firstJson.GetProperty("runs").EnumerateArray().Last()
                    .GetProperty("SelectionGeneration").GetInt32();
                using HttpResponseMessage priceUpdated = await scopedClient.PatchAsJsonAsync(
                    AssetPath(ApiRoutes.Assets.PRICE, assetId), new { price = 2.50m });
                priceUpdated.EnsureSuccessStatusCode();
                await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var price = await db.Assets.AsNoTracking().Where(x => x.Id == assetId)
                        .Select(x => x.Price).SingleAsync();
                    var selectionGeneration = await db.AssetVersions.AsNoTracking()
                        .Where(x => x.AssetId == assetId).Select(x => x.AnalysisSelectionGeneration).SingleAsync();
                    if (price != 2.50m || selectionGeneration != firstSelectionGeneration)
                    {
                        throw new InvalidOperationException("Price-only update altered analysis selection identity.");
                    }
                }
                var versionDeclarationUrl = AssetPath(ApiRoutes.Assets.VERSION_DECLARATION, assetId, assetVersionId);
                using HttpResponseMessage readAgain = await scopedClient.GetAsync(versionDeclarationUrl);
                readAgain.EnsureSuccessStatusCode();
                using var snapshot2 = JsonDocument.Parse(await readAgain.Content.ReadAsStringAsync());
                var revision2 = snapshot2.RootElement.GetProperty("workspaceRevision").GetInt64();
                var changedDeclaration = BuildDeclaration("Changed declaration for live acceptance reuse CAS.");
                using HttpResponseMessage savedAgain = await scopedClient.PutAsJsonAsync(
                    versionDeclarationUrl,
                    new { operationId = Guid.NewGuid(), expectedWorkspaceRevision = revision2, declaration = changedDeclaration });
                savedAgain.EnsureSuccessStatusCode();

                using HttpResponseMessage historicalReviewResponse = await scopedClient.GetAsync(
                    SellerVersionReviewUrl(assetVersionId));
                historicalReviewResponse.EnsureSuccessStatusCode();
                using var historicalReview = JsonDocument.Parse(await historicalReviewResponse.Content.ReadAsStringAsync());
                JsonElement historicalSummary = historicalReview.RootElement.GetProperty("analysisDetails");
                if (historicalSummary.GetProperty("reportId").GetGuid() != admittedReportId
                    || historicalSummary.GetProperty("reportIsCurrent").GetBoolean()
                    || historicalSummary.GetProperty("lifecycle").GetString() is not ("QUEUED" or "RUNNING"))
                {
                    throw new InvalidOperationException("Declaration save did not reconcile analysis while retaining prior report as history.");
                }

                details["historicalSummaryAfterDeclarationSave"] = new
                {
                    reportId = historicalSummary.GetProperty("reportId").GetGuid(),
                    reportIsCurrent = historicalSummary.GetProperty("reportIsCurrent").GetBoolean(),
                    lifecycle = historicalSummary.GetProperty("lifecycle").GetString()
                };

                var second = await WaitForAdmittedReport(factory, assetId, 2, TimeSpan.FromMinutes(25));
                details["second"] = second;
                JsonElement secondJson = JsonSerializer.SerializeToElement(second);
                Guid secondRunId = secondJson.GetProperty("runs").EnumerateArray().Last().GetProperty("Id").GetGuid();
                Guid secondAdmittedReportId = secondJson.GetProperty("reportId").GetGuid();
                using HttpResponseMessage currentReviewResponse = await scopedClient.GetAsync(
                    SellerVersionReviewUrl(assetVersionId));
                currentReviewResponse.EnsureSuccessStatusCode();
                using var currentReview = JsonDocument.Parse(await currentReviewResponse.Content.ReadAsStringAsync());
                JsonElement currentSummary = currentReview.RootElement.GetProperty("analysisDetails");
                if (currentSummary.GetProperty("reportId").GetGuid() != secondAdmittedReportId
                    || !currentSummary.GetProperty("reportIsCurrent").GetBoolean())
                {
                    throw new InvalidOperationException("Owner summary did not switch to the exact newly admitted report after declaration reconciliation.");
                }

                await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    Guid secondReportId = await db.CodeAnalysisReportHeaders.AsNoTracking()
                        .Where(header => header.AnalysisRunId == secondRunId)
                        .Select(header => header.Id).SingleAsync();
                    List<CodeAnalysisReportFinding> findings = await db.CodeAnalysisReportFindings.AsNoTracking()
                        .Where(finding => finding.ReportId == secondReportId).ToListAsync();
                    CodeAnalysisReportFinding? notice = findings.SingleOrDefault(finding => finding.FindingType == "NOTICE_MISSING");
                    if (notice is null || notice.OwnScopePath != "UNKNOWN_SCOPE"
                        || notice.OwnStartByte is not null || notice.OwnEndByte is not null
                        || notice.OwnStartLine is not null || notice.OwnEndLine is not null)
                    {
                        throw new InvalidOperationException("Unmatched declaration scope was not persisted without file coordinates.");
                    }

                    if (findings.Any(finding => finding is { OwnScopePath: "src/calculator.py", FindingType: "LICENSE_UNKNOWN" or "LICENSE_CONTRADICTION" }))
                    {
                        throw new InvalidOperationException("Matched license location did not retain its detected license evidence.");
                    }

                    details["unmatchedDeclarationScope"] = new
                    {
                        notice.FindingType,
                        notice.OwnScopePath,
                        notice.OwnStartByte,
                        notice.OwnEndByte,
                        notice.OwnStartLine,
                        notice.OwnEndLine
                    };
                    details["matchedLicenseScope"] = new { path = "src/calculator.py", hasContradictoryOrUnknownFinding = false };
                }
                JsonElement[] secondReportStages = secondJson.GetProperty("stages").EnumerateArray().ToArray();
                if (secondReportStages.Length != 8 || secondReportStages.Any(x => x.GetProperty("outcome").GetString() != (x.GetProperty("Stage").GetString() == "VERIFIER" ? "NOT_APPLICABLE" : "SUCCEEDED")))
                {
                    throw new InvalidOperationException("Second report did not persist all eight policy-valid stages.");
                }

                if (secondJson.GetProperty("artifacts").GetArrayLength() != 3)
                {
                    throw new InvalidOperationException("Declaration-only reuse report did not persist the next reusable artifact set.");
                }

                if (secondJson.GetProperty("reports").EnumerateArray().Any(x => !x.GetProperty("ReviewEligible").GetBoolean())
                    || secondJson.GetProperty("admissions").EnumerateArray().Any(x => !x.GetProperty("ReviewEligible").GetBoolean()))
                {
                    throw new InvalidOperationException("Declaration-only report did not persist server-issued review eligibility.");
                }

                if (secondJson.GetProperty("processingStatus").GetString() != "READY"
                    || secondJson.GetProperty("assetPublicationSnapshotId").ValueKind != JsonValueKind.Null
                    || secondJson.GetProperty("submissionCount").GetInt32() != 0
                    || secondJson.GetProperty("embeddingJobCount").GetInt32() != 0)
                {
                    throw new InvalidOperationException("Analysis altered publication/submission/embedding state.");
                }

                if (secondJson.GetProperty("auditRows").GetInt32() < 2
                    || secondJson.GetProperty("outboxRows").GetArrayLength() < 1)
                {
                    throw new InvalidOperationException("Upload or analysis audit/outbox effects were not persisted.");
                }

                if (secondReportStages.Single(x => x.GetProperty("Stage").GetString() == "VERIFIER")
                        .GetProperty("tools").GetProperty("mode").GetString() != "DISABLED_BY_EVALUATION")
                {
                    throw new InvalidOperationException("Reuse report did not record the evaluation-disabled verifier mode.");
                }

                JsonElement secondRun = secondJson.GetProperty("runs").EnumerateArray().Last();
                JsonElement firstRun = firstJson.GetProperty("runs").EnumerateArray().Last();
                if (secondRun.GetProperty("DeclarationDigest").GetString() == firstRun.GetProperty("DeclarationDigest").GetString()
                    || secondRun.GetProperty("ContentSha256").GetString() != firstRun.GetProperty("ContentSha256").GetString()
                    || secondRun.GetProperty("PolicyDigest").GetString() != firstRun.GetProperty("PolicyDigest").GetString()
                    || secondRun.GetProperty("MaterialDigest").GetString() != firstRun.GetProperty("MaterialDigest").GetString()
                    || secondRun.GetProperty("ArchiveEvidenceId").GetGuid() != firstRun.GetProperty("ArchiveEvidenceId").GetGuid()
                    || secondRun.GetProperty("MalwareEvidenceId").GetGuid() != firstRun.GetProperty("MalwareEvidenceId").GetGuid()
                    || secondRun.GetProperty("SecurityGeneration").GetInt32() != firstRun.GetProperty("SecurityGeneration").GetInt32()
                    || secondRun.GetProperty("SelectionGeneration").GetInt32() <= firstRun.GetProperty("SelectionGeneration").GetInt32())
                {
                    throw new InvalidOperationException("Version declaration CAS did not change only declaration while price-only update preserved other reuse inputs.");
                }

                JsonElement[] secondStages = secondJson.GetProperty("stages").EnumerateArray().ToArray();
                if (secondStages.Length != 8 || secondStages.Any(x => x.GetProperty("outcome").GetString() != (x.GetProperty("Stage").GetString() == "VERIFIER" ? "NOT_APPLICABLE" : "SUCCEEDED")))
                {
                    throw new InvalidOperationException("Second report did not persist all eight policy-valid stages.");
                }

                JsonElement[] reusableStages = secondStages.Where(x => x.GetProperty("Stage").GetString() is "EXTRACTION" or "TOKEN" or "SEMANTIC").ToArray();
                var firstStagesByName = firstJson.GetProperty("stages").EnumerateArray()
                    .ToDictionary(x => x.GetProperty("Stage").GetString()!, x => x);
                var firstArtifactsByName = firstJson.GetProperty("artifacts").EnumerateArray()
                    .ToDictionary(x => x.GetProperty("Stage").GetString()!, x => x);
                if (reusableStages.Length != 3 || reusableStages.Any(x =>
                        x.GetProperty("ReuseParentRunId").ValueKind != JsonValueKind.String
                        || x.GetProperty("ReuseParentRunId").GetGuid() != firstRunId
                        || x.GetProperty("ReuseParentStageId").GetGuid() != firstStagesByName[x.GetProperty("Stage").GetString()!].GetProperty("Id").GetGuid()
                        || x.GetProperty("tools").GetProperty("reuseArtifactDigest").GetString()
                            != firstArtifactsByName[x.GetProperty("Stage").GetString()!].GetProperty("ArtifactDigest").GetString()))
                {
                    throw new InvalidOperationException("Second report did not bind extraction/token/semantic reuse to admitted parent artifacts.");
                }

                JsonElement semantic = reusableStages.Single(x => x.GetProperty("Stage").GetString() == "SEMANTIC");
                if (!semantic.GetProperty("counts").TryGetProperty("encoderInvocations", out JsonElement invocations) || invocations.GetInt32() != 0)
                {
                    throw new InvalidOperationException("Persisted semantic reuse did not prove encoderInvocations=0.");
                }

                details["submissionSummary"] = currentSummary.Clone();
                if (currentSummary.GetProperty("canSubmit").GetBoolean()
                    || currentSummary.GetProperty("outstandingResponseCount").GetInt32() == 0
                    || !currentSummary.GetProperty("reviewEligible").GetBoolean()
                    || currentSummary.GetProperty("coverageLevel").GetString() != "FULL")
                {
                    throw new InvalidOperationException("The real material report did not require seller responses.");
                }
                object submitRequest;
                long searchRevision;
                await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking()
                        .SingleAsync(w => w.AssetId == assetId && w.AssetVersionId == assetVersionId);
                    searchRevision = await db.Assets.Where(a => a.Id == assetId).Select(a => a.SearchRevision).SingleAsync();
                    submitRequest = new
                    {
                        workspaceId = workspace.Id,
                        expectedWorkspaceRevision = workspace.WorkspaceRevision,
                        expectedCaseRevision = workspace.CaseRevision,
                        operationId = Guid.NewGuid()
                    };
                }
                var submissionUrl = "/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_SUBMISSIONS.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal).Replace("{versionId:guid}", assetVersionId.ToString(), StringComparison.Ordinal);
                using (HttpResponseMessage blocked = await scopedClient.PostAsJsonAsync(submissionUrl, submitRequest))
                {
                    if ((int)blocked.StatusCode != 409 || !(await blocked.Content.ReadAsStringAsync()).Contains(ErrorCodes.ERR_FINDING_RESPONSE_REQUIRED, StringComparison.Ordinal))
                        throw new InvalidOperationException("Material findings did not block submit with the seller-visible reason.");
                }
                ReviewEndpoint responseEndpoint = ReadReviewEndpoint("responses", assetId, assetVersionId);
                ReviewEndpoint evidenceEndpoint = ReadReviewEndpoint("evidence", assetId, assetVersionId);
                details["sellerEndpointContracts"] = new { responses = responseEndpoint, evidence = evidenceEndpoint };
                List<Guid> materialIds;
                await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    List<CodeAnalysisReportFinding> candidates = await db.CodeAnalysisReportFindings.AsNoTracking().Where(f => f.ReportId == secondAdmittedReportId && f.RetrievalEvidenceJson != null).ToListAsync();
                    materialIds = candidates.Where(f => JsonDocument.Parse(f.RetrievalEvidenceJson!).RootElement.GetProperty("material").GetBoolean()).Select(f => f.Id).ToList();
                }
                if (materialIds.Count == 0)
                    throw new InvalidOperationException("Real token retrieval produced no material finding.");
                foreach (Guid findingId in materialIds)
                {
                    SellerResponsesPageDto current = (await ReadReviewResponses(scopedClient, responseEndpoint))!;
                    var responseRequest = new SellerResponseRequest(current.WorkspaceId, secondAdmittedReportId, findingId,
                        current.WorkspaceRevision, current.EvidenceRevision, Guid.NewGuid(), SellerResponseType.NO_EVIDENCE, "");
                    using HttpResponseMessage response = await SendReviewWrite(scopedClient, responseEndpoint, responseRequest);
                    response.EnsureSuccessStatusCode();
                    using HttpResponseMessage responseReplay = await SendReviewWrite(scopedClient, responseEndpoint, responseRequest);
                    responseReplay.EnsureSuccessStatusCode();
                    using HttpResponseMessage staleResponse = await SendReviewWrite(scopedClient, responseEndpoint, responseRequest with { OperationId = Guid.NewGuid(), ExpectedWorkspaceRevision = current.WorkspaceRevision + 1 });
                    if ((int)staleResponse.StatusCode != 409 || !(await staleResponse.Content.ReadAsStringAsync()).Contains(ErrorCodes.ERR_SELLER_EVIDENCE_STALE, StringComparison.Ordinal))
                        throw new InvalidOperationException("Stale evidence did not return the standard 409 reason.");
                }
                SellerResponsesPageDto answered = (await ReadReviewResponses(scopedClient, responseEndpoint))!;
                if (answered.OutstandingCount != 0 || answered.RequiredCount != materialIds.Count)
                    throw new InvalidOperationException("No evidence responses did not satisfy the current snapshot.");
                details["responses"] = new
                {
                    materialCount = materialIds.Count,
                    blockedReason = ErrorCodes.ERR_FINDING_RESPONSE_REQUIRED,
                    answered.OutstandingCount,
                    answered.EvidenceRevision,
                    responseIds = answered.Items.Select(r => r.Id).ToArray(),
                    replayed = true,
                    staleReason = ErrorCodes.ERR_SELLER_EVIDENCE_STALE
                };
                submitRequest = new { workspaceId = answered.WorkspaceId, expectedWorkspaceRevision = answered.WorkspaceRevision, expectedCaseRevision = answered.CaseRevision, operationId = Guid.NewGuid() };
                using HttpResponseMessage submitted = await scopedClient.PostAsJsonAsync(submissionUrl, submitRequest);
                if (!submitted.IsSuccessStatusCode)
                {
                    throw new InvalidOperationException("Real submission failed: HTTP " + (int)submitted.StatusCode + " "
                        + SafeBody(await submitted.Content.ReadAsStringAsync()));
                }
                using var submittedJson = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
                Guid submissionId = submittedJson.RootElement.GetProperty("submissionId").GetGuid();
                using HttpResponseMessage replay = await scopedClient.PostAsJsonAsync(submissionUrl, submitRequest);
                replay.EnsureSuccessStatusCode();
                using var replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
                if (replayJson.RootElement.GetProperty("submissionId").GetGuid() != submissionId)
                {
                    throw new InvalidOperationException("Submission replay changed its frozen case identity.");
                }
                await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    ModerationSubmission submission = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
                    var historyCount = await db.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == submissionId);
                    Asset asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);
                    if (submission.State != ModerationSubmissionState.SUBMITTED || historyCount != 1
                        || submission.CodeAnalysisReportHeaderId != secondAdmittedReportId
                        || asset.CurrentPublicationSnapshotId is not null || asset.SearchRevision != searchRevision
                        || await db.PublicationSnapshots.AnyAsync(p => p.AssetId == assetId))
                    {
                        throw new InvalidOperationException("Submission did not persist one case/history without publication effects.");
                    }
                    details["submission"] = new
                    {
                        submissionId,
                        state = submission.State.ToString(),
                        historyCount,
                        submission.CodeAnalysisReportHeaderId,
                        retainedFindingCount = currentSummary.GetProperty("findingCount").GetInt32(),
                        publicSnapshotId = asset.CurrentPublicationSnapshotId,
                        searchRevisionUnchanged = true,
                        replayed = true
                    };
                }
                SellerResponsesPageDto beforeCorrection = (await ReadReviewResponses(scopedClient, responseEndpoint))!;
                var correction = new SellerEvidenceEditRequest(beforeCorrection.WorkspaceId, secondAdmittedReportId, beforeCorrection.WorkspaceRevision,
                    beforeCorrection.EvidenceRevision, Guid.NewGuid(), "Private evidence-only correction.");
                using HttpResponseMessage corrected = await SendReviewWrite(scopedClient, evidenceEndpoint, correction);
                corrected.EnsureSuccessStatusCode();
                await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    if (await db.ModerationSubmissions.CountAsync(s => s.AssetVersionId == assetVersionId) != 1
                        || (await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId)).State != ModerationSubmissionState.SUPERSEDED
                        || await db.CodeAnalysisRuns.CountAsync(r => r.AssetVersionId == assetVersionId) != 2)
                        throw new InvalidOperationException("Evidence correction did not supersede without creating a case or analysis run.");
                }
                SellerResponsesPageDto afterCorrection = (await ReadReviewResponses(scopedClient, responseEndpoint))!;
                using HttpResponseMessage resubmitted = await scopedClient.PostAsJsonAsync(submissionUrl, new
                {
                    workspaceId = afterCorrection.WorkspaceId,
                    expectedWorkspaceRevision = afterCorrection.WorkspaceRevision,
                    expectedCaseRevision = afterCorrection.CaseRevision,
                    operationId = Guid.NewGuid()
                });
                resubmitted.EnsureSuccessStatusCode();
                using var resubmittedJson = JsonDocument.Parse(await resubmitted.Content.ReadAsStringAsync());
                Guid previousSubmissionId = submissionId;
                submissionId = resubmittedJson.RootElement.GetProperty("submissionId").GetGuid();
                await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    ModerationSubmission linked = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
                    if (linked.PreviousSubmissionId != previousSubmissionId || linked.CodeAnalysisReportHeaderId != secondAdmittedReportId
                        || linked.State != ModerationSubmissionState.SUBMITTED || linked.SellerEvidenceRevision != afterCorrection.EvidenceRevision)
                        throw new InvalidOperationException("Explicit resubmission did not freeze the same report with linked current evidence.");
                    if ((await db.AuditLogs.Select(a => a.MetadataJson).ToListAsync()).Any(value => value != null && value.Contains("Private evidence-only correction.", StringComparison.Ordinal))
                        || (await db.OutboxMessages.Select(o => o.Payload).ToListAsync()).Any(value => value.Contains("Private evidence-only correction.", StringComparison.Ordinal)))
                        throw new InvalidOperationException("Private evidence text leaked into an audit or outbox payload.");
                    details["linkedResubmission"] = new
                    {
                        previousSubmissionId,
                        submissionId,
                        state = linked.State.ToString(),
                        previousState = "SUPERSEDED",
                        linked.CodeAnalysisReportHeaderId,
                        linked.SellerEvidenceRevision,
                        analysisRunCount = 2,
                        historyCount = await db.ModerationSubmissionHistoryEntries.CountAsync(h => h.SubmissionId == submissionId || h.SubmissionId == previousSubmissionId)
                    };
                }
                (HttpClient moderatorClient, var moderatorUsername) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(factory, AppRoles.MODERATOR);
                using (moderatorClient)
                {
                    using HttpResponseMessage queueResponse = await moderatorClient.GetAsync("/" + ApiRoutes.Moderation.QUEUE);
                    queueResponse.EnsureSuccessStatusCode();
                    using var queueJson = JsonDocument.Parse(await queueResponse.Content.ReadAsStringAsync());
                    JsonElement queueItem = queueJson.RootElement.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("submissionId").GetGuid() == submissionId);
                    var queueRevision = queueItem.GetProperty("caseRevision").GetInt64();
                    var claimUrl = "/" + ApiRoutes.Moderation.CLAIM.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
                    var releaseUrl = "/" + ApiRoutes.Moderation.RELEASE.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
                    using HttpResponseMessage ownerClaim = await scopedClient.PostAsJsonAsync(claimUrl, new { operationId = Guid.NewGuid(), expectedCaseRevision = queueRevision });
                    if (ownerClaim.StatusCode != System.Net.HttpStatusCode.Forbidden)
                        throw new InvalidOperationException("Owner claim was not denied.");
                    using HttpResponseMessage claimed = await moderatorClient.PostAsJsonAsync(claimUrl, new { operationId = Guid.NewGuid(), expectedCaseRevision = queueRevision });
                    claimed.EnsureSuccessStatusCode();
                    using var claimedJson = JsonDocument.Parse(await claimed.Content.ReadAsStringAsync());
                    if (claimedJson.RootElement.GetProperty("state").GetString() != "IN_REVIEW")
                        throw new InvalidOperationException("Claim did not start review.");
                    var claimedRevision = claimedJson.RootElement.GetProperty("caseRevision").GetInt64();
                    (HttpClient rival, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(factory, AppRoles.MODERATOR);
                    using (rival)
                    using (HttpResponseMessage contested = await rival.PostAsJsonAsync(claimUrl, new { operationId = Guid.NewGuid(), expectedCaseRevision = claimedRevision }))
                    {
                        if (contested.StatusCode != System.Net.HttpStatusCode.Conflict || !(await contested.Content.ReadAsStringAsync()).Contains(ErrorCodes.ERR_MODERATION_CASE_HELD, StringComparison.Ordinal))
                            throw new InvalidOperationException("Competing claim did not return the held conflict.");
                    }
                    using HttpResponseMessage released = await moderatorClient.PostAsJsonAsync(releaseUrl, new { operationId = Guid.NewGuid(), expectedCaseRevision = claimedRevision });
                    released.EnsureSuccessStatusCode();
                    using var releasedJson = JsonDocument.Parse(await released.Content.ReadAsStringAsync());
                    if (releasedJson.RootElement.GetProperty("state").GetString() != "SUBMITTED")
                        throw new InvalidOperationException("Release did not return the case to the queue.");
                    details["moderatorAssignment"] = new { submissionId, claimedState = "IN_REVIEW", releasedState = "SUBMITTED", competingClaim = "CONFLICT", ownerClaim = "FORBIDDEN" };
                    var caseUrl = "/" + ApiRoutes.Moderation.SUBMISSION_BY_ID.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
                    using HttpResponseMessage moderatorRead = await moderatorClient.GetAsync(caseUrl);
                    moderatorRead.EnsureSuccessStatusCode();
                    if (moderatorRead.Headers.CacheControl is not { Private: true, NoStore: true })
                    {
                        throw new InvalidOperationException("Moderator evidence was cacheable.");
                    }
                    Guid evidenceFindingId;
                    await using (AsyncServiceScope evidenceScope = factory.Services.CreateAsyncScope())
                    {
                        ApplicationDbContext db = evidenceScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        evidenceFindingId = await db.CodeAnalysisReportFindings.Where(f => f.ReportId == secondAdmittedReportId && f.OwnExcerpt != "").Select(f => f.Id).FirstAsync();
                    }
                    var evidenceUrl = ApiRoutes.Moderation.CASE_EVIDENCE.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal).Replace("{kind}", nameof(RestrictedEvidenceKind.OWN_EXCERPT), StringComparison.Ordinal) + "?findingId=" + evidenceFindingId;
                    using (var evidenceRequest = new HttpRequestMessage(HttpMethod.Get, evidenceUrl))
                    using (HttpResponseMessage excerpt = await moderatorClient.SendAsync(evidenceRequest, HttpCompletionOption.ResponseHeadersRead))
                    {
                        excerpt.EnsureSuccessStatusCode();
                        if (excerpt.Headers.CacheControl is not { Private: true, NoStore: true })
                            throw new InvalidOperationException("Restricted evidence was cacheable.");
                        await using AsyncServiceScope auditScope = factory.Services.CreateAsyncScope();
                        ApplicationDbContext auditDb = auditScope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        List<string?> auditMetadata = await auditDb.AuditLogs.AsNoTracking().Where(a => a.Action == AuditActions.RESTRICTED_EVIDENCE_READ && a.ResourceId == submissionId.ToString()).Select(a => a.MetadataJson).ToListAsync();
                        if (!auditMetadata.Any(m => m != null && JsonDocument.Parse(m).RootElement.GetProperty("kind").GetString() == "OWN_EXCERPT"))
                            throw new InvalidOperationException("Evidence audit did not commit before response body consumption.");
                        RestrictedEvidenceDto evidence = (await excerpt.Content.ReadFromJsonAsync<RestrictedEvidenceDto>())!;
                        if (string.IsNullOrEmpty(evidence.Content) || evidence.ReportId != secondAdmittedReportId)
                            throw new InvalidOperationException("Restricted finding excerpt was not report-bound.");
                        if (auditMetadata.Any(m => m != null && m.Contains(evidence.Content, StringComparison.Ordinal)))
                            throw new InvalidOperationException("Private excerpt leaked into audit.");
                        details["restrictedEvidence"] = new { kind = evidence.Kind.ToString(), evidence.FindingId, textBytes = Encoding.UTF8.GetByteCount(evidence.Content), auditCommittedBeforeBody = true, cacheControl = "private, no-store" };
                    }
                    using var moderatorJson = JsonDocument.Parse(await moderatorRead.Content.ReadAsStringAsync());
                    JsonElement moderatorEvidence = moderatorJson.RootElement.GetProperty("retrievalEvidence");
                    if (moderatorEvidence.GetProperty("scoresAvailability").GetString() != "AVAILABLE"
                        || moderatorEvidence.GetProperty("candidates").GetArrayLength() == 0)
                    {
                        throw new InvalidOperationException("Moderator retrieval evidence was unavailable after real admission.");
                    }
                    JsonElement[] moderatorCandidates = moderatorEvidence.GetProperty("candidates").EnumerateArray().ToArray();
                    int similarityCandidateCount = 0;
                    bool noticeCandidateFound = false;
                    foreach (JsonElement candidate in moderatorCandidates)
                    {
                        string? findingType = candidate.GetProperty("findingType").GetString();
                        JsonElement scores = candidate.GetProperty("scores");
                        if (findingType is "SIMILARITY_TOKEN" or "SIMILARITY_SEMANTIC")
                        {
                            similarityCandidateCount++;
                            if (scores.ValueKind != JsonValueKind.Object
                                || !scores.TryGetProperty("tokenRank", out _) || !scores.TryGetProperty("tokenScore", out _)
                                || !scores.TryGetProperty("semanticRank", out _) || !scores.TryGetProperty("semanticScore", out _)
                                || !scores.TryGetProperty("channels", out _) || !scores.TryGetProperty("material", out _)
                                || candidate.GetProperty("referenceItemId").ValueKind != JsonValueKind.String)
                            {
                                throw new InvalidOperationException("Moderator similarity candidate lost a channel or provenance.");
                            }
                        }
                        else
                        {
                            if (scores.ValueKind != JsonValueKind.Null
                                || candidate.GetProperty("scoresAvailability").GetString() != "NOT_APPLICABLE")
                            {
                                throw new InvalidOperationException("Non-similarity finding carried inapplicable retrieval scores.");
                            }

                            noticeCandidateFound |= findingType == "NOTICE_MISSING";
                        }
                    }
                    if (similarityCandidateCount == 0)
                    {
                        throw new InvalidOperationException("Moderator case detail carried no similarity candidate after real admission.");
                    }
                    if (!noticeCandidateFound)
                    {
                        throw new InvalidOperationException("Moderator case detail omitted the notice finding type.");
                    }
                    details["moderatorRetrievalEvidence"] = moderatorEvidence.Clone();
                    await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                    {
                        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        List<CodeAnalysisReportFinding> persisted = await db.CodeAnalysisReportFindings.AsNoTracking()
                            .Where(f => f.ReportId == secondAdmittedReportId && f.RetrievalEvidenceJson != null
                                && (f.FindingType == "SIMILARITY_TOKEN" || f.FindingType == "SIMILARITY_SEMANTIC"))
                            .OrderBy(f => f.FindingKey).ToListAsync();
                        if (persisted.Count != similarityCandidateCount)
                        {
                            throw new InvalidOperationException("Moderator did not return every persisted similarity candidate.");
                        }
                        var returned = moderatorCandidates
                            .Where(c => c.GetProperty("findingType").GetString() is "SIMILARITY_TOKEN" or "SIMILARITY_SEMANTIC")
                            .ToDictionary(c => c.GetProperty("findingKey").GetString()!, c => c.GetProperty("scores"));
                        foreach (CodeAnalysisReportFinding candidate in persisted)
                        {
                            using var detail = JsonDocument.Parse(candidate.RetrievalEvidenceJson!);
                            if (CanonicalJson.Sha256Hex(detail.RootElement) != CanonicalJson.Sha256Hex(returned[candidate.FindingKey]))
                            {
                                throw new InvalidOperationException("Moderator changed persisted channel evidence.");
                            }
                            var parentDetail = await db.CodeAnalysisReportFindings.AsNoTracking()
                                .Where(f => f.ReportId == admittedReportId && f.FindingKey == candidate.FindingKey)
                                .Select(f => f.RetrievalEvidenceJson).SingleAsync();
                            using var parent = JsonDocument.Parse(parentDetail!);
                            if (CanonicalJson.Sha256Hex(parent.RootElement) != CanonicalJson.Sha256Hex(detail.RootElement))
                            {
                                throw new InvalidOperationException("Declaration reuse changed retrieval scores or ranks.");
                            }
                        }
                        details["persistedRetrievalCandidates"] = persisted.Select(f => new
                        {
                            f.FindingKey,
                            f.TokenRank,
                            f.TokenScore,
                            f.SemanticRank,
                            f.SemanticScore,
                            evidence = SafeJson(f.RetrievalEvidenceJson!)
                        }).ToArray();
                        foreach (CodeAnalysisReportFinding candidate in persisted.Where(f => f.ForeignSensitivity == CodeAnalysisFindingSensitivity.PRIVATE))
                        {
                            if (ownerFindings.RootElement.ToString().Contains(candidate.ForeignSourceId!, StringComparison.Ordinal)
                                || currentReview.RootElement.ToString().Contains(candidate.ForeignSourceId!, StringComparison.Ordinal))
                            {
                                throw new InvalidOperationException("Private reference identity reached seller JSON.");
                            }
                        }
                    }
                    AssertSellerRedaction(ownerFindings.RootElement.ToString());
                    AssertSellerRedaction(currentReview.RootElement.ToString());
                    (HttpClient adminClient, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(factory, AppRoles.ADMIN);
                    using (adminClient)
                    {
                        using HttpResponseMessage deniedAdmin = await adminClient.GetAsync(caseUrl);
                        if (deniedAdmin.StatusCode != System.Net.HttpStatusCode.Forbidden)
                        {
                            throw new InvalidOperationException("Admin without Moderator role read private evidence.");
                        }
                    }
                    (HttpClient foreignClient, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(factory);
                    using (foreignClient)
                    {
                        using HttpResponseMessage deniedForeign = await foreignClient.GetAsync(caseUrl);
                        if (deniedForeign.StatusCode != System.Net.HttpStatusCode.Forbidden)
                        {
                            throw new InvalidOperationException("Foreign user read Moderator evidence.");
                        }
                    }
                    await VerifyModerationDecisionLifecycle(factory, scopedClient, moderatorClient, assetId, assetVersionId, secondAdmittedReportId, submissionId, approvalTag, categoryId, details, uphold);
                    string ownerEmail;
                    await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                    {
                        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        User moderator = await db.Users.SingleAsync(u => u.Username == moderatorUsername);
                        moderator.Role = AppRoles.USER;
                        moderator.RoleRevision++;
                        User owner = await db.Users.SingleAsync(u => u.Id == db.Assets.Where(a => a.Id == assetId).Select(a => a.AuthorId).First());
                        owner.Role = AppRoles.MODERATOR;
                        owner.RoleRevision++;
                        ownerEmail = owner.Email;
                        await db.SaveChangesAsync();
                    }
                    using HttpResponseMessage revoked = await moderatorClient.GetAsync(caseUrl);
                    if (revoked.StatusCode != System.Net.HttpStatusCode.Forbidden)
                    {
                        throw new InvalidOperationException("Revoked Moderator retained evidence access through an existing JWT.");
                    }
                    using HttpClient ownerModerator = factory.CreateClient();
                    using HttpResponseMessage ownerLogin = await ownerModerator.PostAsJsonAsync("/" + ApiRoutes.Auth.BASE + "/" + ApiRoutes.Auth.LOGIN, new LoginRequest(ownerEmail, "Password1!"));
                    ownerLogin.EnsureSuccessStatusCode();
                    var ownerTokens = (await ownerLogin.Content.ReadFromJsonAsync<IntegrationTestAuth.TokensResponseDto>())!;
                    ownerModerator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ownerTokens.AccessToken);
                    using HttpResponseMessage deniedOwner = await ownerModerator.GetAsync(caseUrl);
                    if (deniedOwner.StatusCode != System.Net.HttpStatusCode.Forbidden)
                    {
                        throw new InvalidOperationException("Owning Moderator read their own review evidence.");
                    }
                    details["retrievalAccessChecks"] = new
                    {
                        moderator = 200,
                        adminWithoutRole = 403,
                        foreignUser = 403,
                        owningModerator = 403,
                        revokedModerator = 403,
                        sellerRedacted = true,
                        cacheControl = "private, no-store"
                    };
                }
                await VerifyPartialCoverage(factory, categoryId, details);
                details["status"] = "passed";
                await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch (Exception ex)
        {
            details["status"] = "failed";
            details["errorType"] = ex.GetType().FullName;
            details["error"] = "See test failure output; detail omitted to avoid configuration disclosure.";
            await File.WriteAllTextAsync(evidencePath, JsonSerializer.Serialize(details, new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }

    private static string AssetPath(string route, Guid assetId, Guid? versionId = null) =>
        "/" + ApiRoutes.Assets.BASE + "/" + route.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal)
            .Replace("{versionId:guid}", versionId?.ToString(), StringComparison.Ordinal);

    private static string SellerVersionReviewUrl(Guid assetVersionId) =>
        "/" + ApiRoutes.Users.BASE + "/" + ApiRoutes.Users.ME_ASSET_VERSION_REVIEW.Replace("{assetVersionId:guid}", assetVersionId.ToString(), StringComparison.Ordinal);

    private static async Task VerifyModerationDecisionLifecycle(LiveAcceptanceFactory factory, HttpClient seller, HttpClient moderator,
        Guid assetId, Guid assetVersionId, Guid reportId, Guid resubmittedCaseId, string approvalTag, Guid categoryId, Dictionary<string, object?> details, bool uphold)
    {
        const string changesRationale = "Live changes-requested rationale sentinel.";
        const string approvalRationale = "Live approval rationale sentinel.";
        const string correctionText = "Private evidence-only correction after changes requested.";
        ReviewEndpoint responseEndpoint = ReadReviewEndpoint("responses", assetId, assetVersionId);
        ReviewEndpoint evidenceEndpoint = ReadReviewEndpoint("evidence", assetId, assetVersionId);
        var submissionUrl = "/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_SUBMISSIONS.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal).Replace("{versionId:guid}", assetVersionId.ToString(), StringComparison.Ordinal);
        var publicAssetUrl = AssetPath(ApiRoutes.Assets.ID, assetId);
        var downloadUrl = "/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_DOWNLOAD.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal).Replace("{versionId:guid}", assetVersionId.ToString(), StringComparison.Ordinal);
        List<Guid> materialIds = await MaterialFindingIds(factory, reportId);
        if (materialIds.Count == 0)
            throw new InvalidOperationException("Decision flow found no material finding to disposition.");

        ModerationSubmission firstCase = await LoadSubmission(factory, resubmittedCaseId);
        long searchRevisionBefore = await ReadSearchRevision(factory, assetId);
        using (HttpClient anonymous = factory.CreateClient())
        using (HttpResponseMessage hidden = await anonymous.GetAsync(publicAssetUrl))
        {
            if (hidden.IsSuccessStatusCode)
                throw new InvalidOperationException("Asset was public before any approval.");
        }

        await ClaimCase(moderator, resubmittedCaseId, firstCase.CaseRevision);
        firstCase = await LoadSubmission(factory, resubmittedCaseId);
        Guid changesOperation = Guid.NewGuid();
        var changesDispositions = materialIds.Select((id, index) => (id, index == 0 ? "CHANGES_NEEDED" : "EXPLAINED")).ToArray();
        using (HttpResponseMessage changes = await PostDecision(moderator, resubmittedCaseId,
            DecisionBody(changesOperation, firstCase, reportId, "REQUEST_CHANGES", changesRationale, changesDispositions)))
        {
            if (changes.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("REQUEST_CHANGES decision failed: HTTP " + (int)changes.StatusCode + " " + SafeBody(await changes.Content.ReadAsStringAsync()));
        }
        Guid changesDecisionId;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            ModerationDecisionRecord changesRecord = await db.ModerationDecisionRecords.AsNoTracking().SingleAsync(d => d.SubmissionId == resubmittedCaseId);
            changesDecisionId = changesRecord.Id;
            if (changesRecord.Outcome != ModerationSubmissionState.CHANGES_REQUESTED || changesRecord.PublicationSnapshotId is not null)
                throw new InvalidOperationException("REQUEST_CHANGES did not persist a terminal change request without a snapshot.");
            if (await db.PublicationSnapshots.AnyAsync(p => p.AssetId == assetId)
                || (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId)).CurrentPublicationSnapshotId is not null
                || (await db.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId)).SearchRevision != searchRevisionBefore)
                throw new InvalidOperationException("REQUEST_CHANGES changed public state.");
        }
        using (HttpClient anonymous = factory.CreateClient())
        using (HttpResponseMessage stillHidden = await anonymous.GetAsync(publicAssetUrl))
        {
            if (stillHidden.IsSuccessStatusCode)
                throw new InvalidOperationException("REQUEST_CHANGES made the asset public.");
        }

        using (HttpResponseMessage changedReview = await seller.GetAsync(SellerVersionReviewUrl(assetVersionId)))
        {
            changedReview.EnsureSuccessStatusCode();
            using JsonDocument changedJson = JsonDocument.Parse(await changedReview.Content.ReadAsStringAsync());
            JsonElement changedDecision = changedJson.RootElement.GetProperty("decision");
            if (changedDecision.GetProperty("outcome").GetString() != "CHANGES_REQUESTED" || changedDecision.GetProperty("reason").GetString() != changesRationale
                || changedDecision.GetProperty("publication").ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException("Seller review did not project the requested changes with the exact reason and no publication.");
        }
        SellerResponsesPageDto beforeCorrection = (await ReadReviewResponses(seller, responseEndpoint))!;
        var correction = new SellerEvidenceEditRequest(beforeCorrection.WorkspaceId, reportId, beforeCorrection.WorkspaceRevision,
            beforeCorrection.EvidenceRevision, Guid.NewGuid(), correctionText);
        using (HttpResponseMessage corrected = await SendReviewWrite(seller, evidenceEndpoint, correction))
        {
            corrected.EnsureSuccessStatusCode();
        }
        SellerResponsesPageDto afterCorrection = (await ReadReviewResponses(seller, responseEndpoint))!;
        using (HttpResponseMessage resubmitted = await seller.PostAsJsonAsync(submissionUrl, new
        {
            workspaceId = afterCorrection.WorkspaceId,
            expectedWorkspaceRevision = afterCorrection.WorkspaceRevision,
            expectedCaseRevision = afterCorrection.CaseRevision,
            operationId = Guid.NewGuid()
        }))
        {
            if (!resubmitted.IsSuccessStatusCode)
                throw new InvalidOperationException("Resubmission after changes requested failed: HTTP " + (int)resubmitted.StatusCode + " " + SafeBody(await resubmitted.Content.ReadAsStringAsync()));
        }
        using (HttpResponseMessage pendingReview = await seller.GetAsync(SellerVersionReviewUrl(assetVersionId)))
        {
            pendingReview.EnsureSuccessStatusCode();
            using JsonDocument pendingJson = JsonDocument.Parse(await pendingReview.Content.ReadAsStringAsync());
            if (pendingJson.RootElement.GetProperty("moderationState").GetString() != "SUBMITTED"
                || pendingJson.RootElement.GetProperty("decision").ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException("Seller review retained an older decision after resubmission.");
        }
        Guid approvalCaseId;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            approvalCaseId = await db.ModerationSubmissions.AsNoTracking().Where(s => s.PreviousSubmissionId == resubmittedCaseId).Select(s => s.Id).SingleAsync();
        }
        ModerationSubmission approvalCase = await LoadSubmission(factory, approvalCaseId);
        if (approvalCase.State != ModerationSubmissionState.SUBMITTED || approvalCase.CodeAnalysisReportHeaderId != reportId)
            throw new InvalidOperationException("Resubmission after changes requested did not freeze the same report as a submitted case.");

        await ClaimCase(moderator, approvalCaseId, approvalCase.CaseRevision);
        approvalCase = await LoadSubmission(factory, approvalCaseId);
        const string rejectReason = "Live rejection reason sentinel.";
        var approveDispositions = materialIds.Select(id => (id, "EXPLAINED")).ToArray();
        Guid rejectionDecisionId;
        using (HttpResponseMessage rejected = await PostDecision(moderator, approvalCaseId,
            DecisionBody(Guid.NewGuid(), approvalCase, reportId, "REJECT", rejectReason, approveDispositions)))
        {
            if (rejected.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("REJECT failed: HTTP " + (int)rejected.StatusCode + " " + SafeBody(await rejected.Content.ReadAsStringAsync()));
            using JsonDocument body = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            rejectionDecisionId = body.RootElement.GetProperty("decisionId").GetGuid();
        }
        await WaitForOutboxProcessed(factory, ModerationEventIdentity.Decision(rejectionDecisionId, false), TimeSpan.FromMinutes(3));
        using (HttpResponseMessage review = await seller.GetAsync(SellerVersionReviewUrl(assetVersionId)))
        {
            if (review.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Rejected review HTTP " + (int)review.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
            if (!json.RootElement.GetProperty("canAppeal").GetBoolean() || json.RootElement.GetProperty("decision").GetProperty("outcome").GetString() != "REJECTED"
                || json.RootElement.GetProperty("decision").GetProperty("reason").GetString() != rejectReason)
                throw new InvalidOperationException("Rejected seller review did not offer appeal with the exact reason.");
        }
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (!await db.UserNotifications.AnyAsync(n => n.SourceOutboxMessageId == ModerationEventIdentity.Decision(rejectionDecisionId, false) && n.Kind == NotificationKind.MODERATION_REJECTED && n.RecipientUserId == approvalCase.OwnerUserId))
                throw new InvalidOperationException("Rejection notification was not delivered to the seller.");
        }
        SellerResponsesPageDto current = (await ReadReviewResponses(seller, responseEndpoint))!;
        var ordinaryBody = new { workspaceId = current.WorkspaceId, expectedWorkspaceRevision = current.WorkspaceRevision, expectedCaseRevision = current.CaseRevision, operationId = Guid.NewGuid() };
        using (HttpResponseMessage blocked = await seller.PostAsJsonAsync(submissionUrl, ordinaryBody))
        {
            if (blocked.StatusCode != HttpStatusCode.Conflict || !(await blocked.Content.ReadAsStringAsync()).Contains(ErrorCodes.ERR_MODERATION_APPEAL_REQUIRED, StringComparison.Ordinal))
                throw new InvalidOperationException("Unchanged ordinary resubmission did not require an appeal: HTTP " + (int)blocked.StatusCode);
        }
        var appealUrl = AssetPath(ApiRoutes.Assets.VERSION_APPEALS, assetId, assetVersionId);
        const string appealText = "Private appeal rationale sentinel.";
        var appealBody = new { workspaceId = current.WorkspaceId, expectedWorkspaceRevision = current.WorkspaceRevision, expectedCaseRevision = current.CaseRevision, operationId = Guid.NewGuid(), rationale = appealText };
        Guid appealCaseId;
        using (HttpResponseMessage opened = await seller.PostAsJsonAsync(appealUrl, appealBody))
        {
            if (opened.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Appeal request failed: HTTP " + (int)opened.StatusCode + " " + SafeBody(await opened.Content.ReadAsStringAsync()));
            using JsonDocument json = JsonDocument.Parse(await opened.Content.ReadAsStringAsync());
            appealCaseId = json.RootElement.GetProperty("submissionId").GetGuid();
        }
        using (HttpResponseMessage review = await seller.GetAsync(SellerVersionReviewUrl(assetVersionId)))
        {
            if (review.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Pending appeal review HTTP " + (int)review.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
            if (json.RootElement.GetProperty("canAppeal").GetBoolean() || json.RootElement.GetProperty("appeal").GetProperty("state").GetString() != "PENDING_REVIEW"
                || json.RootElement.GetProperty("decision").ValueKind != JsonValueKind.Null)
                throw new InvalidOperationException("Pending appeal projection was incorrect.");
        }
        using (HttpResponseMessage queue = await moderator.GetAsync("/" + ApiRoutes.Moderation.QUEUE))
        {
            if (queue.StatusCode != HttpStatusCode.OK || (await queue.Content.ReadAsStringAsync()).Contains(appealCaseId.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Original reviewer could see the appeal in the queue.");
        }
        ModerationSubmission appealCase = await LoadSubmission(factory, appealCaseId);
        var appealClaimUrl = "/" + ApiRoutes.Moderation.CLAIM.Replace("{id:guid}", appealCaseId.ToString(), StringComparison.Ordinal);
        using (HttpResponseMessage denied = await moderator.PostAsJsonAsync(appealClaimUrl, new { operationId = Guid.NewGuid(), expectedCaseRevision = appealCase.CaseRevision }))
        {
            if (denied.StatusCode != HttpStatusCode.Forbidden || !(await denied.Content.ReadAsStringAsync()).Contains(ErrorCodes.ERR_MODERATION_APPEAL_REVIEWER_EXCLUDED, StringComparison.Ordinal))
                throw new InvalidOperationException("Original reviewer claim was not excluded: HTTP " + (int)denied.StatusCode);
        }
        (HttpClient independent, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(factory, AppRoles.MODERATOR);
        using var independentReviewer = independent;
        using (HttpResponseMessage queue = await independentReviewer.GetAsync("/" + ApiRoutes.Moderation.QUEUE))
        {
            if (queue.StatusCode != HttpStatusCode.OK || !(await queue.Content.ReadAsStringAsync()).Contains(appealCaseId.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Independent reviewer could not see the appeal.");
        }
        using (HttpResponseMessage claimed = await independentReviewer.PostAsJsonAsync(appealClaimUrl, new { operationId = Guid.NewGuid(), expectedCaseRevision = appealCase.CaseRevision }))
        {
            if (claimed.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Independent reviewer claim failed: HTTP " + (int)claimed.StatusCode);
        }
        appealCase = await LoadSubmission(factory, appealCaseId);
        using (HttpResponseMessage changes = await PostDecision(independentReviewer, appealCaseId, DecisionBody(Guid.NewGuid(), appealCase, reportId, "REQUEST_CHANGES", "Not permitted.", approveDispositions)))
        {
            if (changes.StatusCode != HttpStatusCode.Conflict || !(await changes.Content.ReadAsStringAsync()).Contains(ErrorCodes.ERR_MODERATION_APPEAL_DECISION_NOT_ALLOWED, StringComparison.Ordinal))
                throw new InvalidOperationException("Appeal accepted REQUEST_CHANGES: HTTP " + (int)changes.StatusCode);
        }
        if (uphold)
        {
            const string upheldReason = "Live upheld reason sentinel.";
            using HttpResponseMessage rejected = await PostDecision(independentReviewer, appealCaseId, DecisionBody(Guid.NewGuid(), appealCase, reportId, "REJECT", upheldReason, approveDispositions));
            if (rejected.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Appeal uphold failed: HTTP " + (int)rejected.StatusCode + " " + SafeBody(await rejected.Content.ReadAsStringAsync()));
            using JsonDocument outcome = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Guid eventId = ModerationEventIdentity.Decision(outcome.RootElement.GetProperty("decisionId").GetGuid(), true);
            await WaitForOutboxProcessed(factory, eventId, TimeSpan.FromMinutes(3));
            using HttpResponseMessage review = await seller.GetAsync(SellerVersionReviewUrl(assetVersionId));
            if (review.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("Upheld review HTTP " + (int)review.StatusCode);
            using JsonDocument json = JsonDocument.Parse(await review.Content.ReadAsStringAsync());
            if (json.RootElement.GetProperty("canAppeal").GetBoolean() || json.RootElement.GetProperty("appeal").GetProperty("state").GetString() != "UPHELD"
                || json.RootElement.GetProperty("decision").GetProperty("outcome").GetString() != "REJECTED" || json.RootElement.GetProperty("decision").GetProperty("reason").GetString() != upheldReason)
                throw new InvalidOperationException("Upheld seller projection was incorrect.");
            current = (await ReadReviewResponses(seller, responseEndpoint))!;
            using HttpResponseMessage repeat = await seller.PostAsJsonAsync(appealUrl, new { workspaceId = current.WorkspaceId, expectedWorkspaceRevision = current.WorkspaceRevision, expectedCaseRevision = current.CaseRevision, operationId = Guid.NewGuid(), rationale = appealText });
            if (repeat.StatusCode != HttpStatusCode.Conflict || !(await repeat.Content.ReadAsStringAsync()).Contains(ErrorCodes.ERR_MODERATION_APPEAL_EXISTS, StringComparison.Ordinal))
                throw new InvalidOperationException("Upheld appeal could be reopened: HTTP " + (int)repeat.StatusCode);
            await VerifyAppealEffects(factory, appealCaseId, eventId, approvalCase.OwnerUserId, NotificationKind.MODERATION_APPEAL_UPHELD, appealText, upheldReason);
            details["appeal"] = new { state = "UPHELD", appealCaseId, rejectionDecisionId, notification = "MODERATION_APPEAL_UPHELD" };
            return;
        }
        approvalCaseId = appealCaseId;
        approvalCase = appealCase;
        Guid approveOperation = Guid.NewGuid();
        var approveBody = DecisionBody(approveOperation, approvalCase, reportId, "APPROVE", approvalRationale, approveDispositions);
        Guid approvalDecisionId;
        Guid snapshotId;
        using (HttpResponseMessage approved = await PostDecision(independentReviewer, approvalCaseId, approveBody))
        {
            if (approved.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("APPROVE decision failed: HTTP " + (int)approved.StatusCode + " " + SafeBody(await approved.Content.ReadAsStringAsync()));
            using JsonDocument approvedJson = JsonDocument.Parse(await approved.Content.ReadAsStringAsync());
            approvalDecisionId = approvedJson.RootElement.GetProperty("decisionId").GetGuid();
            if (!approvedJson.RootElement.GetProperty("promoted").GetBoolean() || approvedJson.RootElement.GetProperty("replayed").GetBoolean())
                throw new InvalidOperationException("APPROVE was not promoted on its first execution.");
            snapshotId = approvedJson.RootElement.GetProperty("publicationSnapshotId").GetGuid();
        }
        Guid approvalEventId = ModerationEventIdentity.Decision(approvalDecisionId, true);
        await WaitForOutboxProcessed(factory, approvalEventId, TimeSpan.FromMinutes(3));

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Asset asset = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);
            var snapshots = await db.PublicationSnapshots.AsNoTracking().Where(p => p.AssetId == assetId).ToListAsync();
            int decisionCount = await db.ModerationDecisionRecords.CountAsync(d => d.AssetId == assetId);
            int notificationCount = await db.UserNotifications.CountAsync(n => n.SourceOutboxMessageId == approvalEventId);
            bool notificationToOwner = await db.UserNotifications.AnyAsync(n => n.SourceOutboxMessageId == approvalEventId
                && n.RecipientUserId == approvalCase.OwnerUserId);
            if (snapshots.Count != 1 || snapshots[0].Id != snapshotId || asset.CurrentPublicationSnapshotId != snapshotId
                || asset.SearchRevision != searchRevisionBefore + 1 || decisionCount != 3)
                throw new InvalidOperationException("APPROVE did not persist one snapshot, one pointer move and one search revision.");
            using JsonDocument metadata = JsonDocument.Parse(snapshots[0].ApprovedMetadataJson);
            if (asset.Title != "Live encrypted asset upload" || asset.Description != "Task-owned isolated acceptance archive"
                || asset.CategoryId != categoryId || metadata.RootElement.GetProperty("title").GetString() != asset.Title
                || metadata.RootElement.GetProperty("description").GetString() != asset.Description
                || metadata.RootElement.GetProperty("categoryId").GetGuid() != categoryId
                || !metadata.RootElement.GetProperty("tags").EnumerateArray().Any(t => t.GetString() == approvalTag)
                || !await db.AssetTags.AnyAsync(t => t.AssetId == assetId && t.Tag.Name == approvalTag))
                throw new InvalidOperationException("APPROVE did not promote the frozen metadata and tag.");
            if (notificationCount != 1 || !notificationToOwner)
                throw new InvalidOperationException("APPROVE decision notification was not delivered exactly once to the owner.");
        }

        int outboxBeforeReplay;
        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            outboxBeforeReplay = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().OutboxMessages
                .CountAsync(m => m.Type == OutboxMessageTypes.MODERATION_DECISION);
        }
        using (HttpResponseMessage replay = await PostDecision(independentReviewer, approvalCaseId, approveBody))
        {
            if (replay.StatusCode != HttpStatusCode.OK)
                throw new InvalidOperationException("APPROVE replay did not return the original decision: HTTP " + (int)replay.StatusCode);
            using JsonDocument replayJson = JsonDocument.Parse(await replay.Content.ReadAsStringAsync());
            if (!replayJson.RootElement.GetProperty("replayed").GetBoolean() || replayJson.RootElement.GetProperty("decisionId").GetGuid() != approvalDecisionId)
                throw new InvalidOperationException("APPROVE replay did not return the original decision identity.");
        }
        using (HttpResponseMessage changedPayload = await PostDecision(independentReviewer, approvalCaseId,
            DecisionBody(approveOperation, approvalCase, reportId, "APPROVE", "Changed approval rationale.", approveDispositions)))
        {
            if (changedPayload.StatusCode != HttpStatusCode.Conflict || !(await changedPayload.Content.ReadAsStringAsync()).Contains(ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT, StringComparison.Ordinal))
                throw new InvalidOperationException("Changed payload with a reused operation did not conflict.");
        }
        using (HttpResponseMessage secondDecision = await PostDecision(independentReviewer, approvalCaseId,
            DecisionBody(Guid.NewGuid(), approvalCase, reportId, "APPROVE", approvalRationale, approveDispositions)))
        {
            if (secondDecision.StatusCode != HttpStatusCode.Conflict || !(await secondDecision.Content.ReadAsStringAsync()).Contains(ErrorCodes.ERR_MODERATION_DECISION_EXISTS, StringComparison.Ordinal))
                throw new InvalidOperationException("Second decision on a terminal case did not return the decision-exists conflict.");
        }

        await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            OutboxMessage message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == approvalEventId);
            IOutboxMessageHandler handler = scope.ServiceProvider.GetServices<IOutboxMessageHandler>()
                .Single(h => h.MessageType == OutboxMessageTypes.MODERATION_DECISION);
            await handler.Handle(message, CancellationToken.None);
            await handler.Handle(message, CancellationToken.None);
            if (await db.UserNotifications.CountAsync(n => n.SourceOutboxMessageId == approvalEventId) != 1
                || await db.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.MODERATION_DECISION) != outboxBeforeReplay
                || await db.PublicationSnapshots.CountAsync(p => p.AssetId == assetId) != 1
                || await db.ModerationDecisionRecords.CountAsync(d => d.AssetId == assetId) != 3)
                throw new InvalidOperationException("Replayed or repeated decision effects created new rows.");
        }

        using (HttpClient anonymous = factory.CreateClient())
        {
            using HttpResponseMessage publicDetail = await anonymous.GetAsync(publicAssetUrl);
            if (!publicDetail.IsSuccessStatusCode)
                throw new InvalidOperationException("Approved asset was not visible on the public detail route.");
            if (!(await publicDetail.Content.ReadAsStringAsync()).Contains("Live encrypted asset upload", StringComparison.Ordinal))
                throw new InvalidOperationException("Public detail did not expose the approved metadata.");
            using HttpResponseMessage unentitledDownload = await anonymous.GetAsync(downloadUrl);
            if (unentitledDownload.IsSuccessStatusCode)
                throw new InvalidOperationException("Approved version bytes were downloadable without an entitlement.");
        }
        (HttpClient buyer, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(factory);
        using (buyer)
        using (HttpResponseMessage catalog = await buyer.GetAsync("/" + ApiRoutes.Assets.BASE + ApiRoutes.Assets.LIST + "?page=1&pageSize=100&search=" + Uri.EscapeDataString("Live encrypted asset upload")))
        {
            if (catalog.StatusCode != HttpStatusCode.OK || !(await catalog.Content.ReadAsStringAsync()).Contains(assetId.ToString(), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Buyer catalog did not expose the approved appeal asset: HTTP " + (int)catalog.StatusCode);
        }

        string sellerReviewJson;
        using (HttpResponseMessage sellerReview = await seller.GetAsync(SellerVersionReviewUrl(assetVersionId)))
        {
            sellerReview.EnsureSuccessStatusCode();
            sellerReviewJson = await sellerReview.Content.ReadAsStringAsync();
        }
        using (JsonDocument projected = JsonDocument.Parse(sellerReviewJson))
        {
            JsonElement decision = projected.RootElement.GetProperty("decision");
            if (projected.RootElement.GetProperty("appeal").GetProperty("state").GetString() != "APPROVED" || decision.GetProperty("outcome").GetString() != "APPROVED" || decision.GetProperty("reason").ValueKind != JsonValueKind.Null
                || decision.GetProperty("publication").GetString() != "PUBLISHED")
                throw new InvalidOperationException("Seller review did not project the approved decision as published without a reason.");

            int changesReasonHistoryEntries = projected.RootElement.GetProperty("history").EnumerateArray().Count(entry =>
                entry.GetProperty("decisionOutcome").GetString() == "CHANGES_REQUESTED"
                && entry.GetProperty("decisionReason").GetString() == changesRationale);
            if (changesReasonHistoryEntries == 0)
                throw new InvalidOperationException("Seller history did not expose the allowed changes-requested reason.");
            if (CountOccurrences(sellerReviewJson, changesRationale) != changesReasonHistoryEntries)
                throw new InvalidOperationException("Changes-requested rationale appeared outside seller history decision reasons.");
        }
        await VerifyAppealEffects(factory, appealCaseId, approvalEventId, approvalCase.OwnerUserId, NotificationKind.MODERATION_APPROVED, appealText, approvalRationale);
        SellerResponsesPageDto finalResponses = (await ReadReviewResponses(seller, responseEndpoint))!;
        string sellerResponsesJson = JsonSerializer.Serialize(finalResponses);
        foreach (string json in new[] { sellerReviewJson, sellerResponsesJson })
        {
            if (json.Contains(approvalRationale, StringComparison.Ordinal)
                || json.Contains("Live disposition", StringComparison.Ordinal))
                throw new InvalidOperationException("Approval rationale or disposition text reached seller JSON.");
            AssertSellerRedaction(json);
        }
        if (sellerResponsesJson.Contains(changesRationale, StringComparison.Ordinal))
            throw new InvalidOperationException("Changes-requested rationale escaped its seller history field.");

        details["moderationDecision"] = new
        {
            changesDecisionId,
            approvalDecisionId,
            snapshotId,
            requestChangesCaseId = resubmittedCaseId,
            approvalCaseId,
            materialDispositions = materialIds.Count,
            searchRevisionBefore,
            searchRevisionAfter = searchRevisionBefore + 1,
            publicBeforeApproval = false,
            publicAfterApproval = true,
            unentitledVersionDownload = "DENIED",
            changesRequestedPublicEffect = "NONE",
            notificationsForApproval = 1,
            replay = "ORIGINAL_DECISION_NO_NEW_ROWS",
            changedPayloadReusedOperation = ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT,
            secondDecision = ErrorCodes.ERR_MODERATION_DECISION_EXISTS,
            repeatedDispatchNotifications = 1,
            sellerJsonRedacted = true,
            sellerDecisionProjection = new { outcome = "APPROVED", reason = (string?)null, publication = "PUBLISHED" },
            pendingSecondVersion = "NOT_EXERCISED_LIVE"
        };
    }

    private static async Task VerifyAppealEffects(LiveAcceptanceFactory factory, Guid appealId, Guid eventId, Guid ownerId, NotificationKind kind, string appealText, string rationale)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        if (await db.UserNotifications.CountAsync(n => n.SourceOutboxMessageId == eventId && n.RecipientUserId == ownerId && n.Kind == kind) != 1)
            throw new InvalidOperationException("Appeal decision notification was not delivered exactly once.");
        List<AuditLog> audits = await db.AuditLogs.AsNoTracking().Where(a => a.ResourceId == appealId.ToString() && (a.Action == AuditActions.APPEAL_OPENED || a.Action == AuditActions.CASE_DECIDED)).ToListAsync();
        string[] privateText = [appealText, rationale, "Live rejection reason sentinel.", "Live changes-requested rationale sentinel.", "Live disposition sentinel."];
        if (audits.Count != 2 || audits.Any(a => privateText.Any(text => (a.MetadataJson ?? "").Contains(text, StringComparison.Ordinal))))
            throw new InvalidOperationException("Appeal audit rows were missing or contained private text.");
        List<string> payloads = await db.OutboxMessages.AsNoTracking().Where(m => m.Type == OutboxMessageTypes.MODERATION_LIFECYCLE || m.Type == OutboxMessageTypes.MODERATION_DECISION).Select(m => m.Payload).ToListAsync();
        if (payloads.Any(p => privateText.Any(text => p.Contains(text, StringComparison.Ordinal))))
            throw new InvalidOperationException("Appeal outbox contained private text.");
    }

    private static object DecisionBody(Guid operationId, ModerationSubmission submission, Guid reportId, string outcome, string rationale,
        IEnumerable<(Guid FindingId, string Disposition)> dispositions) => new
        {
            operationId,
            basis = new
            {
                versionId = submission.AssetVersionId,
                contentSha256 = submission.ContentSha256,
                analysisReportId = reportId,
                declarationRevision = submission.DeclarationRevision,
                materialMetadataRevision = submission.MaterialMetadataRevision,
                policyVersion = submission.PolicyVersion,
                sellerEvidenceRevision = submission.SellerEvidenceRevision,
                caseRevision = submission.CaseRevision
            },
            outcome,
            rationale,
            dispositions = dispositions.Select(d => new { findingId = d.FindingId, disposition = d.Disposition, rationale = "Live disposition sentinel." }).ToArray(),
            manualPlanCompletionReference = (string?)null
        };

    private static async Task<HttpResponseMessage> PostDecision(HttpClient moderator, Guid submissionId, object body) =>
        await moderator.PostAsJsonAsync("/" + ApiRoutes.Moderation.DECISION.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal), body);

    private static async Task ClaimCase(HttpClient moderator, Guid submissionId, long expectedCaseRevision)
    {
        var claimUrl = "/" + ApiRoutes.Moderation.CLAIM.Replace("{id:guid}", submissionId.ToString(), StringComparison.Ordinal);
        using HttpResponseMessage claimed = await moderator.PostAsJsonAsync(claimUrl, new { operationId = Guid.NewGuid(), expectedCaseRevision });
        if (!claimed.IsSuccessStatusCode)
            throw new InvalidOperationException("Moderator claim for the decision flow failed: HTTP " + (int)claimed.StatusCode + " " + SafeBody(await claimed.Content.ReadAsStringAsync()));
    }

    private static async Task<ModerationSubmission> LoadSubmission(WebApplicationFactory<Program> factory, Guid submissionId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
    }

    private static async Task<long> ReadSearchRevision(WebApplicationFactory<Program> factory, Guid assetId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Assets.AsNoTracking().SingleAsync(a => a.Id == assetId)).SearchRevision;
    }

    private static async Task<List<Guid>> MaterialFindingIds(WebApplicationFactory<Program> factory, Guid reportId)
    {
        await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
        List<CodeAnalysisReportFinding> candidates = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().CodeAnalysisReportFindings
            .AsNoTracking().Where(f => f.ReportId == reportId && f.RetrievalEvidenceJson != null).ToListAsync();
        return candidates.Where(f => JsonDocument.Parse(f.RetrievalEvidenceJson!).RootElement.GetProperty("material").GetBoolean()).Select(f => f.Id).ToList();
    }

    private static async Task WaitForOutboxProcessed(WebApplicationFactory<Program> factory, Guid messageId, TimeSpan timeout)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
            OutboxMessageStatus? status = await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().OutboxMessages.AsNoTracking()
                .Where(m => m.Id == messageId).Select(m => (OutboxMessageStatus?)m.Status).SingleOrDefaultAsync();
            if (status == OutboxMessageStatus.PROCESSED)
                return;
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
        throw new InvalidOperationException("Decision outbox message was not processed before the timeout.");
    }

    private static async Task<object> WaitForAdmittedReport(WebApplicationFactory<Program> factory, Guid assetId, int requiredReports, TimeSpan timeout)
    {
        DateTimeOffset until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            await using AsyncServiceScope scope = factory.Services.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(x => x.AssetId == assetId);
            List<CodeAnalysisRun> runs = await db.CodeAnalysisRuns.AsNoTracking().Where(x => x.AssetId == assetId).OrderBy(x => x.CreatedAt).ToListAsync();
            List<CodeAnalysisReportHeader> reports = await db.CodeAnalysisReportHeaders.AsNoTracking().Where(x => x.AssetId == assetId && x.IsFinalized).OrderBy(x => x.CreatedAt).ToListAsync();
            List<AssetProcessingJob> jobs = await db.AssetProcessingJobs.AsNoTracking().Where(x => x.AssetId == assetId).OrderBy(x => x.CreatedAt).ToListAsync();
            Guid? publicationSnapshotId = await db.Assets.AsNoTracking()
                .Where(x => x.Id == assetId).Select(x => x.CurrentPublicationSnapshotId).SingleAsync();
            var submissionCount = await db.ModerationSubmissions.AsNoTracking()
                .CountAsync(x => x.AssetId == assetId);
            var embeddingJobCount = jobs.Count(x => x.Type == AssetProcessingJobType.EMBEDDING_GENERATION);
            if (reports.Count >= requiredReports)
            {
                Guid reportId = reports[^1].Id;
                List<CodeAnalysisReportStage> stages = await db.CodeAnalysisReportStages.AsNoTracking().Where(x => x.ReportId == reportId).OrderBy(x => x.Stage).ToListAsync();
                List<CodeAnalysisReportAdmission> admissions = await db.CodeAnalysisReportAdmissions.AsNoTracking().Where(x => x.ReportId == reportId).ToListAsync();
                List<CodeAnalysisReuseArtifact> artifacts = await db.CodeAnalysisReuseArtifacts.AsNoTracking().Where(x => x.SourceReportId == reportId).ToListAsync();
                List<SecurityEvidenceRecord> evidence = await db.SecurityEvidenceRecords.AsNoTracking().Where(x => x.AssetVersionId == version.Id).OrderBy(x => x.CreatedAt).ToListAsync();
                var audits = await db.AuditLogs.AsNoTracking().Where(x => x.ResourceId == assetId.ToString()).CountAsync();
                List<OutboxMessage> outbox = await db.OutboxMessages.AsNoTracking().Where(x => x.Payload.Contains(assetId.ToString())).ToListAsync();
                return new
                {
                    assetVersionId = version.Id,
                    contentSha256 = version.ContentSha256,
                    processingStatus = version.ProcessingStatus.ToString(),
                    assetPublicationSnapshotId = publicationSnapshotId,
                    submissionCount,
                    embeddingJobCount,
                    runCount = runs.Count,
                    runs = runs.Select(x => new { x.Id, state = x.State.ToString(), x.InputDigest, x.ContentSha256, x.PolicyDigest, x.DeclarationDigest, x.MaterialDigest, x.ParentRunId, x.SelectionGeneration, x.ArchiveEvidenceId, x.MalwareEvidenceId, x.SecurityGeneration }),
                    reportId = reports[^1].Id,
                    reports = reports.Select(x => new { x.Id, x.AnalysisRunId, x.PythonReportId, x.ReportDigest, x.ReviewEligible, x.IsFinalized }),
                    admissions = admissions.Select(x => new { x.Id, x.AdmittedBy, x.ReviewEligible, x.ReviewEligibilityReason, x.InputDigest }),
                    stages = stages.Select(x => new { x.Id, x.Stage, outcome = x.Outcome.ToString(), x.Applicability, x.InputSha256, x.OutputSha256, x.ReuseParentRunId, x.ReuseParentStageId, counts = SafeJson(x.CountsJson), tools = SafeJson(x.ToolIdentityJson) }),
                    artifacts = artifacts.Select(x => new { x.Id, x.SourceRunId, x.SourceStageId, x.Stage, x.ArtifactDigest, x.PayloadBytes }),
                    securityEvidence = evidence.Select(x => new { x.Id, evidenceKind = x.EvidenceKind.ToString(), evidenceOutcome = x.Outcome.ToString(), x.ContentSha256, x.ScopeComplete, x.ImplementationIdentity, x.EngineIdentity, x.EngineVersion, x.SignatureIdentity, x.SignatureObservedAt, x.SignatureValidUntil, x.EvidenceDigest, x.ScannedByteCount }),
                    jobs = jobs.Select(x => new { x.Id, type = x.Type.ToString(), status = x.Status.ToString(), x.Stage, x.AttemptCount, x.ErrorCode, x.AnalysisRunId }),
                    auditRows = audits,
                    outboxRows = outbox.Select(x => new { x.Id, x.Type, status = x.Status.ToString(), x.AttemptCount, x.ProcessedAt }),
                    reportCount = reports.Count
                };
            }
            if (jobs.Any(x => x.Status == AssetProcessingJobStatus.FAILED))
            {
                var failure = jobs.Where(x => x.Status == AssetProcessingJobStatus.FAILED)
                    .Select(x => new { type = x.Type.ToString(), x.Stage, x.ErrorCode, x.ErrorSummary, x.AttemptCount }).ToArray();
                throw new InvalidOperationException("Processing job failed: " + JsonSerializer.Serialize(failure));
            }
            AssetProcessingJob? analysisRefusal = jobs.FirstOrDefault(x => x is { Type: AssetProcessingJobType.CODE_ANALYSIS, Status: AssetProcessingJobStatus.RETRY_SCHEDULED }
                                                                           && (x.ErrorCode == "ANALYSIS_RESPONSE_INVALID" || x.ErrorCode == "ANALYSIS_CAPACITY_UNAVAILABLE"));
            if (analysisRefusal is not null)
            {
                throw new InvalidOperationException("Analysis job refused: " + analysisRefusal.ErrorCode + " " + analysisRefusal.ErrorSummary);
            }

            await Task.Delay(1000);
        }
        throw new TimeoutException("Timed out waiting for report count " + requiredReports);
    }

    // This is the same checked-in endpoint contract imported by the BFF route handlers.
    internal sealed record ReviewEndpoint(string Path, string WriteMethod, string? ReadMethod);
    internal static ReviewEndpoint ReadReviewEndpoint(string name, Guid assetId, Guid versionId)
    {
        const string relative = "asblock-frontend/lib/seller/seller-review-endpoints.json";
        DirectoryInfo? directory = new(Directory.GetCurrentDirectory());
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, relative)))
        { directory = directory.Parent; }
        if (directory is null)
            throw new InvalidOperationException("Shared seller endpoint contract was not found.");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory.FullName, relative)));
        JsonElement endpoint = document.RootElement.GetProperty(name);
        string path = endpoint.GetProperty("path").GetString()!.Replace("{assetId}", assetId.ToString(), StringComparison.Ordinal)
            .Replace("{versionId}", versionId.ToString(), StringComparison.Ordinal);
        return new(path, endpoint.GetProperty("writeMethod").GetString()!, endpoint.TryGetProperty("readMethod", out JsonElement read) ? read.GetString() : null);
    }
    private static async Task<HttpResponseMessage> SendReviewWrite(HttpClient client, ReviewEndpoint endpoint, object body)
    {
        using var request = new HttpRequestMessage(new HttpMethod(endpoint.WriteMethod), endpoint.Path) { Content = JsonContent.Create(body) };
        return await client.SendAsync(request);
    }
    private static async Task<SellerResponsesPageDto?> ReadReviewResponses(HttpClient client, ReviewEndpoint endpoint)
    {
        using var request = new HttpRequestMessage(new HttpMethod(endpoint.ReadMethod!), endpoint.Path + "?page=1");
        using HttpResponseMessage response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SellerResponsesPageDto>();
    }

    private static async Task VerifyPartialCoverage(LiveAcceptanceFactory factory, Guid categoryId, Dictionary<string, object?> details)
    {
        (HttpClient owner, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(factory);
        (HttpClient moderator, _) = await IntegrationTestAuth.RegisterVerifiedWithRoleAsync(factory, AppRoles.MODERATOR);
        using (owner)
        using (moderator)
        {
            using HttpResponseMessage created = await owner.PostAsJsonAsync("/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.DRAFT_CREATE, new { operationId = Guid.NewGuid(), title = "Manual documentation review", description = "Bounded stored documentation omission", price = 1.25m, categoryId, downloadLimitPerHour = 10 });
            created.EnsureSuccessStatusCode();
            using JsonDocument draft = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
            Guid assetId = draft.RootElement.GetProperty("assetId").GetGuid();
            Guid workspaceId = draft.RootElement.GetProperty("workspaceId").GetGuid();
            using HttpResponseMessage declared = await owner.PutAsJsonAsync(AssetPath(ApiRoutes.Assets.DECLARATION, assetId), new { operationId = Guid.NewGuid(), expectedWorkspaceRevision = draft.RootElement.GetProperty("workspaceRevision").GetInt64(), declaration = BuildDeclaration() });
            declared.EnsureSuccessStatusCode();
            using JsonDocument declaration = JsonDocument.Parse(await declared.Content.ReadAsStringAsync());
            using var form = new MultipartFormDataContent();
            form.Add(new StringContent("PERSONAL"), "LicenseCode");
            form.Add(new StringContent("Readable documentation scope"), "ReleaseNotes");
            form.Add(new StringContent(workspaceId.ToString()), "WorkspaceId");
            form.Add(new StringContent(declaration.RootElement.GetProperty("workspaceRevision").GetInt64().ToString()), "ExpectedWorkspaceRevision");
            var archive = new ByteArrayContent(CreateArchive(true));
            archive.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            form.Add(archive, "file", "coverage-acceptance.zip");
            using HttpResponseMessage uploaded = await owner.PostAsync(AssetPath(ApiRoutes.Assets.VERSIONS, assetId), form);
            uploaded.EnsureSuccessStatusCode();
            object produced = await WaitForAdmittedReport(factory, assetId, 1, TimeSpan.FromMinutes(25));
            Guid versionId;
            Guid reportId;
            await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
            {
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                CodeAnalysisReportHeader report = await db.CodeAnalysisReportHeaders.AsNoTracking().SingleAsync(r => r.AssetId == assetId);
                if (report.CoverageLevel != CodeAnalysisCoverageLevel.PARTIAL || report.OmissionCount != 1 || !report.ReviewEligible)
                    throw new InvalidOperationException("Real documentation archive did not produce admitted PARTIAL coverage.");
                versionId = report.AssetVersionId;
                reportId = report.Id;
            }
            ReviewEndpoint responseEndpoint = ReadReviewEndpoint("responses", assetId, versionId);
            SellerResponsesPageDto page = (await ReadReviewResponses(owner, responseEndpoint))!;
            List<Guid> material;
            await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
            {
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                List<CodeAnalysisReportFinding> findings = await db.CodeAnalysisReportFindings.Where(f => f.ReportId == reportId && f.RetrievalEvidenceJson != null).ToListAsync();
                material = findings.Where(f => JsonDocument.Parse(f.RetrievalEvidenceJson!).RootElement.GetProperty("material").GetBoolean()).Select(f => f.Id).ToList();
            }
            foreach (Guid id in material)
            {
                page = (await ReadReviewResponses(owner, responseEndpoint))!;
                using HttpResponseMessage response = await SendReviewWrite(owner, responseEndpoint, new SellerResponseRequest(page.WorkspaceId, reportId, id, page.WorkspaceRevision, page.EvidenceRevision, Guid.NewGuid(), SellerResponseType.NO_EVIDENCE, ""));
                response.EnsureSuccessStatusCode();
            }
            page = (await ReadReviewResponses(owner, responseEndpoint))!;
            var submitUrl = "/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.VERSION_SUBMISSIONS.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal).Replace("{versionId:guid}", versionId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage blocked = await owner.PostAsJsonAsync(submitUrl, new { workspaceId = page.WorkspaceId, expectedWorkspaceRevision = page.WorkspaceRevision, expectedCaseRevision = page.CaseRevision, operationId = Guid.NewGuid() });
            if ((int)blocked.StatusCode != 409 || !(await blocked.Content.ReadAsStringAsync()).Contains(ErrorCodes.ERR_REVIEW_COVERAGE_REQUIRED, StringComparison.Ordinal))
                throw new InvalidOperationException("Incomplete coverage did not block before assessment.");
            ReviewEndpoint coverageEndpoint = ReadReviewEndpoint("coverage", assetId, versionId);
            var requestBody = new CoverageReviewCreateRequest(Guid.NewGuid(), page.WorkspaceId, reportId, page.WorkspaceRevision);
            using HttpResponseMessage requested = await SendReviewWrite(owner, coverageEndpoint, requestBody);
            requested.EnsureSuccessStatusCode();
            CoverageReviewSaved saved = (await requested.Content.ReadFromJsonAsync<CoverageReviewSaved>())!;
            using HttpResponseMessage requestReplay = await SendReviewWrite(owner, coverageEndpoint, requestBody);
            requestReplay.EnsureSuccessStatusCode();
            foreach (var kind in new[] { "COVERAGE_INVENTORY", "ARCHIVE_README" })
            {
                var evidencePath = ApiRoutes.Moderation.COVERAGE_EVIDENCE.Replace("{id:guid}", saved.RequestId.ToString(), StringComparison.Ordinal).Replace("{kind}", kind, StringComparison.Ordinal);
                using HttpResponseMessage evidence = await moderator.GetAsync(evidencePath);
                evidence.EnsureSuccessStatusCode();
                if (evidence.Headers.CacheControl is not { Private: true, NoStore: true })
                    throw new InvalidOperationException("Request evidence was cacheable.");
                RestrictedEvidenceDto content = (await evidence.Content.ReadFromJsonAsync<RestrictedEvidenceDto>())!;
                if (content.Truncated || string.IsNullOrEmpty(content.Content))
                    throw new InvalidOperationException("Complete readable request evidence was unavailable.");
            }
            var assessment = new CoverageAssessmentRequest(Guid.NewGuid(), saved.RequestRevision, saved.TupleDigest, CoverageAssessmentOutcome.ACCEPTED,
                ["Markdown"], [new("README.md", "Markdown", "Documentation; no executable core logic", true, RestrictedEvidenceKind.ARCHIVE_README)], true,
                "Read complete stored documentation and compare omitted scope with inventory.", "manual-review/readme-complete", "Private documentation assessment rationale.");
            var assessmentPath = ApiRoutes.Moderation.COVERAGE_ASSESSMENT.Replace("{id:guid}", saved.RequestId.ToString(), StringComparison.Ordinal);
            using HttpResponseMessage assessed = await moderator.PostAsJsonAsync(assessmentPath, assessment);
            if (!assessed.IsSuccessStatusCode)
                throw new InvalidOperationException("Coverage assessment failed: " + (int)assessed.StatusCode + " " + SafeBody(await assessed.Content.ReadAsStringAsync()));
            using HttpResponseMessage review = await owner.GetAsync(SellerVersionReviewUrl(versionId));
            review.EnsureSuccessStatusCode();
            var safe = await review.Content.ReadAsStringAsync();
            using JsonDocument status = JsonDocument.Parse(safe);
            if (!status.RootElement.GetProperty("canSubmit").GetBoolean() || status.RootElement.GetProperty("coverageReview").GetProperty("status").GetString() != "ACCEPTED" || safe.Contains(assessment.Rationale, StringComparison.Ordinal))
                throw new InvalidOperationException("Accepted scope did not enable seller-safe submission.");
            page = (await ReadReviewResponses(owner, responseEndpoint))!;
            using HttpResponseMessage submitted = await owner.PostAsJsonAsync(submitUrl, new { workspaceId = page.WorkspaceId, expectedWorkspaceRevision = page.WorkspaceRevision, expectedCaseRevision = page.CaseRevision, operationId = Guid.NewGuid() });
            submitted.EnsureSuccessStatusCode();
            using JsonDocument submittedJson = JsonDocument.Parse(await submitted.Content.ReadAsStringAsync());
            Guid submissionId = submittedJson.RootElement.GetProperty("submissionId").GetGuid();
            Guid assessmentId;
            await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
            {
                ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                ModerationSubmission row = await db.ModerationSubmissions.AsNoTracking().SingleAsync(s => s.Id == submissionId);
                assessmentId = await db.CoverageAssessments.Where(a => a.RequestId == saved.RequestId).Select(a => a.Id).SingleAsync();
                if (row.State != ModerationSubmissionState.SUBMITTED || row.CoverageAssessmentId != assessmentId || await db.PublicationSnapshots.AnyAsync(p => p.AssetId == assetId))
                    throw new InvalidOperationException("Partial submission did not freeze accepted assessment without publication.");
            }
            page = (await ReadReviewResponses(owner, responseEndpoint))!;
            using HttpResponseMessage corrected = await owner.PutAsJsonAsync(AssetPath(ApiRoutes.Assets.VERSION_DECLARATION, assetId, versionId), new { operationId = Guid.NewGuid(), expectedWorkspaceRevision = page.WorkspaceRevision, declaration = BuildDeclaration("Documentation attribution clarified") });
            corrected.EnsureSuccessStatusCode();
            using HttpResponseMessage invalidated = await owner.GetAsync(SellerVersionReviewUrl(versionId));
            invalidated.EnsureSuccessStatusCode();
            using JsonDocument invalid = JsonDocument.Parse(await invalidated.Content.ReadAsStringAsync());
            if (invalid.RootElement.GetProperty("coverageReview").GetProperty("status").GetString() != "INVALIDATED" || invalid.RootElement.GetProperty("canSubmit").GetBoolean())
                throw new InvalidOperationException("Tuple change did not invalidate accepted assessment.");
            await WaitForAdmittedReport(factory, assetId, 2, TimeSpan.FromMinutes(25));
            details["manualCoverage"] = new
            {
                assetId,
                versionId,
                reportId,
                saved.RequestId,
                assessmentId,
                submissionId,
                coverage = "PARTIAL",
                omittedPath = "README.md",
                genuineProductionReport = true,
                requestEndpoint = coverageEndpoint,
                assessment = "ACCEPTED",
                submittedState = "SUBMITTED",
                invalidatedAfterDeclarationChange = true,
                rawSourceOrRationaleRecorded = false
            };
        }
    }

    private static byte[] CreateArchive(bool manualScope = false)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "src/calculator.py", "# SPDX-License-Identifier: MIT\n# Copyright (c) 2026 Live acceptance\ndef mean(items):\n    return sum(items) / len(items)\n\n\ndef total(items):\n    acc = 0\n    for entry in items:\n        acc = acc + entry\n    return acc\n");
            if (manualScope)
                Add(zip, "README.md", "# SPDX-License-Identifier: MIT\nDocumentation for this arithmetic helper. No executable logic.");
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static object BuildDeclaration(string? changes = null) => new
    {
        ownContributionSummary = "Original small arithmetic helper created for isolated acceptance.",
        ownChanges = changes ?? "Created for Live encrypted asset upload acceptance.",
        earlierWork = (string?)null,
        redistributionAcknowledged = true,
        disclosurePolicyVersion = "disclosure-v1",
        components = new[]
        {
            new { componentId = "live-acceptance-own", name = "Live acceptance source", packagePathOrRange = (string?)null,
                sourceUrl = "https://example.invalid/live-acceptance", knownVersion = (string?)null, license = "MIT",
                noticeLocations = Array.Empty<string>(), modifications = "Original", permissionEvidenceReferences = Array.Empty<string>(),
                origin = "OWN_CONTRIBUTION" },
            new { componentId = "live-acceptance-license", name = "Matched license", packagePathOrRange = (string?)"src/calculator.py",
                sourceUrl = "https://example.invalid/live-acceptance-license", knownVersion = (string?)null, license = "MIT",
                noticeLocations = new[] { "src/calculator.py" }, modifications = "Original", permissionEvidenceReferences = Array.Empty<string>(),
                origin = "OWN_CONTRIBUTION" }
        }
    };

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        for (int index = text.IndexOf(value, StringComparison.Ordinal); index >= 0; index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }

    private static void AssertSellerRedaction(string json)
    {
        foreach (var field in new[] { "tokenRank", "tokenScore", "semanticRank", "semanticScore", "channels", "material",
            "queryFileId", "referenceItemId", "referenceSnapshotId", "retrieval", "retrievalSummary", "retrievalEvidence" })
        {
            if (json.Contains("\"" + field + "\":", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("Moderator-only field reached seller JSON: " + field);
            }
        }
    }

    private static string SafeBody(string body) => body.Length > 1200 ? body[..1200] : body;
    private static object SafeJson(string json) { try { return JsonDocument.Parse(json).RootElement.Clone(); } catch { return new { malformed = true }; } }
}

internal sealed class LiveAcceptanceFactory(LiveAcceptanceOptions live) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureLogging(logging => logging.AddConsole().SetMinimumLevel(LogLevel.Information));
        builder.UseEnvironment("IntegrationTesting");
        builder.ConfigureAppConfiguration((_, config) => config.AddJsonFile(live.ConfigurationPath, optional: false, reloadOnChange: false));
        builder.UseSetting("Jwt:Key", "integration_test_jwt_signing_key_min_32_chars!");
        builder.UseSetting("Jwt:Issuer", "AssetBlock.Integration");
        builder.UseSetting("Jwt:Audience", "AssetBlock.Integration.Api");
        builder.UseSetting("Jwt:AccessTokenMinutes", "60");
        builder.UseSetting("Jwt:RefreshTokenDays", "7");
        builder.UseSetting("Jwt:HubAudience", "AssetBlock.Integration.Hub");
        builder.UseSetting("Jwt:HubTokenSeconds", "90");
        builder.UseSetting("ConnectionStrings:DefaultConnection", live.ConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", "");
        builder.UseSetting("DataProtection:KeysPath", live.DataProtectionKeysPath);
        builder.UseSetting("DataProtection:ProtectionMode", "None");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender, RecordingEmailSender>();
            services.RemoveAll<IPaymentService>();
            services.AddSingleton<IPaymentService, FakePaymentService>();

        });
    }
}

internal sealed class LiveAcceptanceFactAttribute : FactAttribute
{
    public LiveAcceptanceFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable(LiveAcceptanceOptions.ENABLED_VARIABLE), "1", StringComparison.Ordinal))
        {
            Skip = "Live acceptance is opt-in; set ASBLOCK_LIVE_ACCEPTANCE=1 with explicit configuration and evidence paths.";
        }
    }
}

internal sealed record LiveAcceptanceOptions(string ConfigurationPath, string EvidencePath, string DataProtectionKeysPath, string ConnectionString)
{
    public const string ENABLED_VARIABLE = "ASBLOCK_LIVE_ACCEPTANCE";
    public const string CONFIGURATION_PATH_VARIABLE = "ASBLOCK_LIVE_ACCEPTANCE_CONFIG_PATH";
    private const string EVIDENCE_PATH_VARIABLE = "ASBLOCK_LIVE_ACCEPTANCE_EVIDENCE_PATH";
    private const string DATA_PROTECTION_KEYS_PATH_VARIABLE = "ASBLOCK_LIVE_ACCEPTANCE_DATA_PROTECTION_PATH";

    public static LiveAcceptanceOptions Resolve() => Resolve(
        Environment.GetEnvironmentVariable(ENABLED_VARIABLE),
        Environment.GetEnvironmentVariable(CONFIGURATION_PATH_VARIABLE),
        Environment.GetEnvironmentVariable(EVIDENCE_PATH_VARIABLE),
        Environment.GetEnvironmentVariable(DATA_PROTECTION_KEYS_PATH_VARIABLE));

    internal static LiveAcceptanceOptions Resolve(string? enabled, string? configurationPath, string? evidencePath, string? dataProtectionKeysPath)
    {
        if (!string.Equals(enabled, "1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Set {ENABLED_VARIABLE}=1 to run live acceptance.");
        }

        if (string.IsNullOrWhiteSpace(configurationPath))
        {
            throw new InvalidOperationException($"{CONFIGURATION_PATH_VARIABLE} must point to an existing JSON settings file.");
        }

        var fullConfigurationPath = Path.GetFullPath(configurationPath);
        if (!File.Exists(fullConfigurationPath))
        {
            throw new InvalidOperationException($"{CONFIGURATION_PATH_VARIABLE} must point to an existing JSON settings file.");
        }

        string connectionString;
        string configuredKeysPath;
        try
        {
            using var settings = JsonDocument.Parse(File.ReadAllText(fullConfigurationPath));
            connectionString = settings.RootElement.GetProperty("ConnectionStrings")
                .GetProperty("DefaultConnection").GetString() ?? "";
            configuredKeysPath = settings.RootElement.GetProperty("DataProtection")
                .GetProperty("KeysPath").GetString() ?? "";
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new InvalidOperationException($"{CONFIGURATION_PATH_VARIABLE} must include ConnectionStrings:DefaultConnection and DataProtection:KeysPath.");
        }

        if (string.IsNullOrWhiteSpace(connectionString) || connectionString.StartsWith('<'))
        {
            throw new InvalidOperationException($"{CONFIGURATION_PATH_VARIABLE} must include a usable isolated ConnectionStrings:DefaultConnection.");
        }

        if (string.IsNullOrWhiteSpace(configuredKeysPath))
        {
            throw new InvalidOperationException($"{CONFIGURATION_PATH_VARIABLE} must include DataProtection:KeysPath.");
        }

        if (string.IsNullOrWhiteSpace(evidencePath))
        {
            throw new InvalidOperationException($"{EVIDENCE_PATH_VARIABLE} must point to an evidence file path.");
        }

        if (string.IsNullOrWhiteSpace(dataProtectionKeysPath))
        {
            throw new InvalidOperationException($"{DATA_PROTECTION_KEYS_PATH_VARIABLE} must point to a dedicated key-ring directory outside source control.");
        }

        var fullDataProtectionKeysPath = Path.GetFullPath(dataProtectionKeysPath);
        if (!string.Equals(Path.GetFullPath(configuredKeysPath), fullDataProtectionKeysPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{DATA_PROTECTION_KEYS_PATH_VARIABLE} must match DataProtection:KeysPath in the settings file.");
        }

        var leaf = Path.GetFileName(fullDataProtectionKeysPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!leaf.StartsWith("assetblock-dataprotection-keys", StringComparison.OrdinalIgnoreCase)
            && !leaf.Equals("dataprotection-keys", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{DATA_PROTECTION_KEYS_PATH_VARIABLE} must name a dedicated AssetBlock key-ring directory.");
        }

        var fullEvidencePath = Path.GetFullPath(evidencePath);
        var evidenceDirectory = Path.GetDirectoryName(fullEvidencePath);
        if (string.IsNullOrWhiteSpace(evidenceDirectory))
        {
            throw new InvalidOperationException($"{EVIDENCE_PATH_VARIABLE} must include an evidence directory.");
        }

        Directory.CreateDirectory(evidenceDirectory);
        return new LiveAcceptanceOptions(fullConfigurationPath, fullEvidencePath, fullDataProtectionKeysPath, connectionString);
    }
}


public sealed class LiveAcceptanceOptionsTests
{
    [Fact]
    public void Resolve_WhenLiveAcceptanceIsNotEnabled_ShouldFailBeforeLoadingServices()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            LiveAcceptanceOptions.Resolve(null, null, null, null));

        Assert.Contains("ASBLOCK_LIVE_ACCEPTANCE=1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WhenEnabledButConfigurationIsMissing_ShouldFailClearly()
    {
        var missingConfiguration = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var evidencePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "evidence.json");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            LiveAcceptanceOptions.Resolve("1", missingConfiguration, evidencePath, Path.Combine(Path.GetTempPath(), "assetblock-dataprotection-keys-test")));

        Assert.Contains(LiveAcceptanceOptions.CONFIGURATION_PATH_VARIABLE, error.Message, StringComparison.Ordinal);
        Assert.Contains("existing JSON settings file", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(evidencePath)!));
    }
}
