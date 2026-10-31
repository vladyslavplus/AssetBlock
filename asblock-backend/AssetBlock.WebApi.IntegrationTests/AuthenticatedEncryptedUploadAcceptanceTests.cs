using System.IO.Compression;
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
using AssetBlock.Infrastructure.Persistence;
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
        var evidencePath = live.EvidencePath;
        var details = new Dictionary<string, object?>();
        try
        {
            await using var factory = new LiveAcceptanceFactory(live);
            (HttpClient scopedClient, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(factory);
            using (scopedClient)
            {
                var categoryId = Guid.NewGuid();
                await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                {
                    ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.Database.CanConnectAsync();
                    db.Categories.Add(new Category { Id = categoryId, Name = "LiveAcceptance Acceptance", Slug = "live-acceptance-" + categoryId.ToString("N") });
                    await db.SaveChangesAsync();
                }

                using HttpResponseMessage created = await scopedClient.PostAsJsonAsync("/api/assets/drafts",
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

                var declaration = BuildDeclaration();
                using HttpResponseMessage saved = await scopedClient.PutAsJsonAsync(
                    "/api/assets/" + assetId + "/declaration",
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

                using HttpResponseMessage upload = await scopedClient.PostAsync($"/api/assets/{assetId}/versions", multipart);
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
                    $"/api/users/me/asset-versions/{assetVersionId}/review");
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
                    $"/api/users/me/asset-versions/{assetVersionId}/analysis/reports/{admittedReportId}/findings?page=1");
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
                    "/api/assets/" + assetId + "/price", new { price = 2.50m });
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
                var versionDeclarationUrl = "/api/assets/" + assetId + "/versions/" + assetVersionId + "/declaration";
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
                    $"/api/users/me/asset-versions/{assetVersionId}/review");
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
                    $"/api/users/me/asset-versions/{assetVersionId}/review");
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
                var submissionUrl = $"/api/assets/{assetId}/versions/{assetVersionId}/submissions";
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
                    var caseUrl = $"/api/moderation/submissions/{submissionId}";
                    using HttpResponseMessage moderatorRead = await moderatorClient.GetAsync(caseUrl);
                    moderatorRead.EnsureSuccessStatusCode();
                    if (moderatorRead.Headers.CacheControl is not { Private: true, NoStore: true })
                    {
                        throw new InvalidOperationException("Moderator evidence was cacheable.");
                    }
                    using var moderatorJson = JsonDocument.Parse(await moderatorRead.Content.ReadAsStringAsync());
                    JsonElement moderatorEvidence = moderatorJson.RootElement.GetProperty("retrievalEvidence");
                    if (moderatorEvidence.GetProperty("scoresAvailability").GetString() != "AVAILABLE"
                        || moderatorEvidence.GetProperty("candidates").GetArrayLength() == 0)
                    {
                        throw new InvalidOperationException("Moderator retrieval evidence was unavailable after real admission.");
                    }
                    foreach (JsonElement candidate in moderatorEvidence.GetProperty("candidates").EnumerateArray())
                    {
                        JsonElement scores = candidate.GetProperty("scores");
                        if (!scores.TryGetProperty("tokenRank", out _) || !scores.TryGetProperty("tokenScore", out _)
                            || !scores.TryGetProperty("semanticRank", out _) || !scores.TryGetProperty("semanticScore", out _)
                            || !scores.TryGetProperty("channels", out _) || !scores.TryGetProperty("material", out _)
                            || candidate.GetProperty("referenceItemId").ValueKind != JsonValueKind.String)
                        {
                            throw new InvalidOperationException("Moderator candidate lost a channel or provenance.");
                        }
                    }
                    details["moderatorRetrievalEvidence"] = moderatorEvidence.Clone();
                    await using (AsyncServiceScope scope = factory.Services.CreateAsyncScope())
                    {
                        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                        List<CodeAnalysisReportFinding> persisted = await db.CodeAnalysisReportFindings.AsNoTracking()
                            .Where(f => f.ReportId == secondAdmittedReportId && f.RetrievalEvidenceJson != null)
                            .OrderBy(f => f.FindingKey).ToListAsync();
                        if (persisted.Count != moderatorEvidence.GetProperty("candidates").GetArrayLength())
                        {
                            throw new InvalidOperationException("Moderator did not return every persisted retrieval candidate.");
                        }
                        var returned = moderatorEvidence.GetProperty("candidates").EnumerateArray()
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
                    using HttpResponseMessage ownerLogin = await ownerModerator.PostAsJsonAsync("/api/auth/login", new LoginRequest(ownerEmail, "Password1!"));
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
    private sealed record ReviewEndpoint(string Path, string WriteMethod, string? ReadMethod);
    private static ReviewEndpoint ReadReviewEndpoint(string name, Guid assetId, Guid versionId)
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

    private static byte[] CreateArchive()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "src/calculator.py", "# SPDX-License-Identifier: MIT\n# Copyright (c) 2026 Live acceptance\ndef mean(items):\n    return sum(items) / len(items)\n\n\ndef total(items):\n    acc = 0\n    for entry in items:\n        acc = acc + entry\n    return acc\n");
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
