using System.IO.Compression;
#pragma warning disable IDE0007, IDE0008, IDE0011
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
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
                Guid categoryId = Guid.NewGuid();
                await using (var scope = factory.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    await db.Database.CanConnectAsync();
                    db.Categories.Add(new Category { Id = categoryId, Name = "LiveAcceptance Acceptance", Slug = "live-acceptance-" + categoryId.ToString("N") });
                    await db.SaveChangesAsync();
                }

                byte[] archive = CreateArchive();
                using var multipart = new MultipartFormDataContent();
                multipart.Add(new StringContent("Live encrypted asset upload"), "Title");
                multipart.Add(new StringContent("Task-owned isolated acceptance archive"), "Description");
                multipart.Add(new StringContent("1.25"), "Price");
                multipart.Add(new StringContent(categoryId.ToString()), "CategoryId");
                multipart.Add(new StringContent("PERSONAL"), "LicenseCode");
                var file = new ByteArrayContent(archive);
                file.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
                multipart.Add(file, "file", "live-acceptance.zip");

                using HttpResponseMessage upload = await scopedClient.PostAsync("/api/assets/upload", multipart);
                string uploadBody = await upload.Content.ReadAsStringAsync();
                details["uploadStatus"] = (int)upload.StatusCode;
                if (!upload.IsSuccessStatusCode)
                    throw new InvalidOperationException("Real upload endpoint failed: HTTP " + (int)upload.StatusCode + " " + SafeBody(uploadBody));
                using JsonDocument uploadJson = JsonDocument.Parse(uploadBody);
                Guid assetId = uploadJson.RootElement.GetProperty("id").GetGuid();
                details["assetId"] = assetId;

                using HttpResponseMessage getDeclaration = await scopedClient.GetAsync("/api/assets/" + assetId + "/declaration");
                getDeclaration.EnsureSuccessStatusCode();
                using JsonDocument declarationSnapshot = JsonDocument.Parse(await getDeclaration.Content.ReadAsStringAsync());
                var snap = declarationSnapshot.RootElement;
                Guid workspaceId = snap.GetProperty("workspaceId").GetGuid();
                long revision = snap.GetProperty("workspaceRevision").GetInt64();
                object declaration = BuildDeclaration();
                using HttpResponseMessage saved = await scopedClient.PutAsJsonAsync(
                    "/api/assets/" + assetId + "/declaration",
                    new { operationId = Guid.NewGuid(), expectedWorkspaceRevision = revision, declaration });
                saved.EnsureSuccessStatusCode();

                var first = await WaitForAdmittedReport(factory, assetId, 1, TimeSpan.FromMinutes(25));
                details["first"] = first;
                var firstJson = JsonSerializer.SerializeToElement(first);
                var firstStages = firstJson.GetProperty("stages").EnumerateArray().ToArray();
                if (firstStages.Length != 8 || firstStages.Any(x => x.GetProperty("outcome").GetString() != "SUCCEEDED"))
                    throw new InvalidOperationException("Initial report did not persist all eight successful stages.");
                if (firstJson.GetProperty("artifacts").GetArrayLength() != 3)
                    throw new InvalidOperationException("Initial admitted report did not persist all three reusable artifacts.");
                if (firstJson.GetProperty("reports").EnumerateArray().Any(x => x.GetProperty("CanAuthorizePublication").GetBoolean())
                    || firstJson.GetProperty("admissions").EnumerateArray().Any(x => x.GetProperty("CanAuthorizePublication").GetBoolean()))
                    throw new InvalidOperationException("Initial report granted publication authority.");
                if (firstStages.Single(x => x.GetProperty("Stage").GetString() == "VERIFIER")
                    .GetProperty("tools").GetProperty("mode").GetString() != "PENDING_RESEARCH_EVALUATION")
                    throw new InvalidOperationException("Initial report left pending research evaluation mode.");
                var firstRunId = firstJson.GetProperty("runs").EnumerateArray().Last().GetProperty("Id").GetGuid();
                details["workspaceId"] = workspaceId;

                var firstSelectionGeneration = firstJson.GetProperty("runs").EnumerateArray().Last()
                    .GetProperty("SelectionGeneration").GetInt32();
                using HttpResponseMessage priceUpdated = await scopedClient.PatchAsJsonAsync(
                    "/api/assets/" + assetId + "/price", new { price = 2.50m });
                priceUpdated.EnsureSuccessStatusCode();
                await using (var scope = factory.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var price = await db.Assets.AsNoTracking().Where(x => x.Id == assetId)
                        .Select(x => x.Price).SingleAsync();
                    var selectionGeneration = await db.AssetVersions.AsNoTracking()
                        .Where(x => x.AssetId == assetId).Select(x => x.AnalysisSelectionGeneration).SingleAsync();
                    if (price != 2.50m || selectionGeneration != firstSelectionGeneration)
                        throw new InvalidOperationException("Price-only update altered analysis selection identity.");
                }
                Guid assetVersionId = firstJson.GetProperty("assetVersionId").GetGuid();
                var versionDeclarationUrl = "/api/assets/" + assetId + "/versions/" + assetVersionId + "/declaration";
                using HttpResponseMessage readAgain = await scopedClient.GetAsync(versionDeclarationUrl);
                readAgain.EnsureSuccessStatusCode();
                using JsonDocument snapshot2 = JsonDocument.Parse(await readAgain.Content.ReadAsStringAsync());
                var revision2 = snapshot2.RootElement.GetProperty("workspaceRevision").GetInt64();
                object changedDeclaration = BuildDeclaration("Changed declaration for live acceptance reuse CAS.");
                using HttpResponseMessage savedAgain = await scopedClient.PutAsJsonAsync(
                    versionDeclarationUrl,
                    new { operationId = Guid.NewGuid(), expectedWorkspaceRevision = revision2, declaration = changedDeclaration });
                savedAgain.EnsureSuccessStatusCode();

                // Declaration saves use workspace CAS. Schedule the changed immutable input through the
                // same transactional product enqueuer used by upload and durable outbox recovery.
                var latestProofRun = firstJson.GetProperty("runs").EnumerateArray().Last();
                AnalysisRunEnqueueResult secondEnqueue;
                await using (var scope = factory.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    var enqueueTime = await db.Database.SqlQueryRaw<DateTimeOffset>(
                        """SELECT clock_timestamp() AS "Value" """).FirstAsync();
                    var enqueuer = scope.ServiceProvider.GetRequiredService<IAnalysisRunEnqueuer>();
                    secondEnqueue = await enqueuer.Enqueue(
                        assetId,
                        firstJson.GetProperty("assetVersionId").GetGuid(),
                        null,
                        latestProofRun.GetProperty("ArchiveEvidenceId").GetGuid(),
                        latestProofRun.GetProperty("MalwareEvidenceId").GetGuid(),
                        latestProofRun.GetProperty("SecurityGeneration").GetInt32(),
                        firstJson.GetProperty("contentSha256").GetString()!,
                        enqueueTime);
                }
                if (secondEnqueue.Rejection != AnalysisEnqueueRejection.NONE || secondEnqueue.IsReplay)
                    throw new InvalidOperationException("Changed declaration did not create a fresh scheduled analysis run: " + secondEnqueue.Rejection);
                details["secondEnqueue"] = new { secondEnqueue.AnalysisRunId, secondEnqueue.AnalysisJobId, secondEnqueue.IsReplay, secondEnqueue.Rejection };

                var second = await WaitForAdmittedReport(factory, assetId, 2, TimeSpan.FromMinutes(25));
                details["second"] = second;
                var secondJson = JsonSerializer.SerializeToElement(second);
                var secondRunId = secondJson.GetProperty("runs").EnumerateArray().Last().GetProperty("Id").GetGuid();
                await using (var scope = factory.Services.CreateAsyncScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
                    Guid secondReportId = await db.CodeAnalysisReportHeaders.AsNoTracking()
                        .Where(header => header.AnalysisRunId == secondRunId)
                        .Select(header => header.Id).SingleAsync();
                    var findings = await db.CodeAnalysisReportFindings.AsNoTracking()
                        .Where(finding => finding.ReportId == secondReportId).ToListAsync();
                    var notice = findings.SingleOrDefault(finding => finding.FindingType == "NOTICE_MISSING");
                    if (notice is null || notice.OwnScopePath != "UNKNOWN_SCOPE"
                        || notice.OwnStartByte is not null || notice.OwnEndByte is not null
                        || notice.OwnStartLine is not null || notice.OwnEndLine is not null)
                    {
                        throw new InvalidOperationException("Unmatched declaration scope was not persisted without file coordinates.");
                    }

                    if (findings.Any(finding => finding.OwnScopePath == "LICENSE"
                        && finding.FindingType is "LICENSE_UNKNOWN" or "LICENSE_CONTRADICTION"))
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
                    details["matchedLicenseScope"] = new { path = "LICENSE", hasContradictoryOrUnknownFinding = false };
                }
                var secondReportStages = secondJson.GetProperty("stages").EnumerateArray().ToArray();
                if (secondReportStages.Length != 8 || secondReportStages.Any(x => x.GetProperty("outcome").GetString() != "SUCCEEDED"))
                    throw new InvalidOperationException("Second report did not persist all eight successful stages.");
                if (secondJson.GetProperty("artifacts").GetArrayLength() != 3)
                    throw new InvalidOperationException("Declaration-only reuse report did not persist the next reusable artifact set.");
                if (secondJson.GetProperty("reports").EnumerateArray().Any(x => x.GetProperty("CanAuthorizePublication").GetBoolean())
                    || secondJson.GetProperty("admissions").EnumerateArray().Any(x => x.GetProperty("CanAuthorizePublication").GetBoolean()))
                    throw new InvalidOperationException("Declaration-only report granted publication authority.");
                if (secondJson.GetProperty("processingStatus").GetString() != "READY"
                    || secondJson.GetProperty("assetPublicationSnapshotId").ValueKind != JsonValueKind.Null
                    || secondJson.GetProperty("submissionCount").GetInt32() != 0
                    || secondJson.GetProperty("embeddingJobCount").GetInt32() != 0)
                    throw new InvalidOperationException("Analysis altered publication/submission/embedding state.");
                if (secondJson.GetProperty("auditRows").GetInt32() < 2
                    || secondJson.GetProperty("outboxRows").GetArrayLength() < 1)
                    throw new InvalidOperationException("Upload or analysis audit/outbox effects were not persisted.");
                if (secondReportStages.Single(x => x.GetProperty("Stage").GetString() == "VERIFIER")
                    .GetProperty("tools").GetProperty("mode").GetString() != "PENDING_RESEARCH_EVALUATION")
                    throw new InvalidOperationException("Reuse report left pending research evaluation mode.");
                var secondRun = secondJson.GetProperty("runs").EnumerateArray().Last();
                var firstRun = firstJson.GetProperty("runs").EnumerateArray().Last();
                if (secondRun.GetProperty("DeclarationDigest").GetString() == firstRun.GetProperty("DeclarationDigest").GetString()
                    || secondRun.GetProperty("ContentSha256").GetString() != firstRun.GetProperty("ContentSha256").GetString()
                    || secondRun.GetProperty("PolicyDigest").GetString() != firstRun.GetProperty("PolicyDigest").GetString()
                    || secondRun.GetProperty("MaterialDigest").GetString() != firstRun.GetProperty("MaterialDigest").GetString()
                    || secondRun.GetProperty("ArchiveEvidenceId").GetGuid() != firstRun.GetProperty("ArchiveEvidenceId").GetGuid()
                    || secondRun.GetProperty("MalwareEvidenceId").GetGuid() != firstRun.GetProperty("MalwareEvidenceId").GetGuid()
                    || secondRun.GetProperty("SecurityGeneration").GetInt32() != firstRun.GetProperty("SecurityGeneration").GetInt32()
                    || secondRun.GetProperty("SelectionGeneration").GetInt32() <= firstRun.GetProperty("SelectionGeneration").GetInt32())
                    throw new InvalidOperationException("Version declaration CAS did not change only declaration while price-only update preserved other reuse inputs.");
                var secondStages = secondJson.GetProperty("stages").EnumerateArray().ToArray();
                if (secondStages.Length != 8 || secondStages.Any(x => x.GetProperty("outcome").GetString() != "SUCCEEDED"))
                    throw new InvalidOperationException("Second report did not persist all eight successful stages.");
                var reusableStages = secondStages.Where(x => x.GetProperty("Stage").GetString() is "EXTRACTION" or "TOKEN" or "SEMANTIC").ToArray();
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
                    throw new InvalidOperationException("Second report did not bind extraction/token/semantic reuse to admitted parent artifacts.");
                var semantic = reusableStages.Single(x => x.GetProperty("Stage").GetString() == "SEMANTIC");
                if (!semantic.GetProperty("counts").TryGetProperty("encoderInvocations", out var invocations) || invocations.GetInt32() != 0)
                    throw new InvalidOperationException("Persisted semantic reuse did not prove encoderInvocations=0.");
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
        var until = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < until)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var version = await db.AssetVersions.AsNoTracking().SingleAsync(x => x.AssetId == assetId);
            var runs = await db.CodeAnalysisRuns.AsNoTracking().Where(x => x.AssetId == assetId).OrderBy(x => x.CreatedAt).ToListAsync();
            var reports = await db.CodeAnalysisReportHeaders.AsNoTracking().Where(x => x.AssetId == assetId && x.IsFinalized).OrderBy(x => x.CreatedAt).ToListAsync();
            var jobs = await db.AssetProcessingJobs.AsNoTracking().Where(x => x.AssetId == assetId).OrderBy(x => x.CreatedAt).ToListAsync();
            Guid? publicationSnapshotId = await db.Assets.AsNoTracking()
                .Where(x => x.Id == assetId).Select(x => x.CurrentPublicationSnapshotId).SingleAsync();
            var submissionCount = await db.ModerationSubmissions.AsNoTracking()
                .CountAsync(x => x.AssetId == assetId);
            var embeddingJobCount = jobs.Count(x => x.Type == AssetProcessingJobType.EMBEDDING_GENERATION);
            if (reports.Count >= requiredReports)
            {
                Guid reportId = reports[reports.Count - 1].Id;
                var stages = await db.CodeAnalysisReportStages.AsNoTracking().Where(x => x.ReportId == reportId).OrderBy(x => x.Stage).ToListAsync();
                var admissions = await db.CodeAnalysisReportAdmissions.AsNoTracking().Where(x => x.ReportId == reportId).ToListAsync();
                var artifacts = await db.CodeAnalysisReuseArtifacts.AsNoTracking().Where(x => x.SourceReportId == reportId).ToListAsync();
                var evidence = await db.SecurityEvidenceRecords.AsNoTracking().Where(x => x.AssetVersionId == version.Id).OrderBy(x => x.CreatedAt).ToListAsync();
                var audits = await db.AuditLogs.AsNoTracking().Where(x => x.ResourceId == assetId.ToString()).CountAsync();
                var outbox = await db.OutboxMessages.AsNoTracking().Where(x => x.Payload.Contains(assetId.ToString())).ToListAsync();
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
                    reports = reports.Select(x => new { x.Id, x.AnalysisRunId, x.PythonReportId, x.ReportDigest, x.CanAuthorizePublication, x.IsFinalized }),
                    admissions = admissions.Select(x => new { x.Id, x.AdmittedBy, x.CanAuthorizePublication, x.PublicationAuthorityReason, x.InputDigest }),
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
            var analysisRefusal = jobs.FirstOrDefault(x => x.Type == AssetProcessingJobType.CODE_ANALYSIS
                && x.Status == AssetProcessingJobStatus.RETRY_SCHEDULED
                && (x.ErrorCode == "ANALYSIS_RESPONSE_INVALID" || x.ErrorCode == "ANALYSIS_CAPACITY_UNAVAILABLE"));
            if (analysisRefusal is not null)
                throw new InvalidOperationException("Analysis job refused: " + analysisRefusal.ErrorCode + " " + analysisRefusal.ErrorSummary);
            await Task.Delay(1000);
        }
        throw new TimeoutException("Timed out waiting for report count " + requiredReports);
    }

    private static byte[] CreateArchive()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "README.md", "# Live acceptance\nBenign task-local source archive.\n");
            Add(zip, "LICENSE", "MIT License\n\nCopyright (c) 2026 Live acceptance\n");
            Add(zip, "src/calculator.py", "def add(a, b):\n    return a + b\n");
            Add(zip, "pyproject.toml", "[project]\nname = \"live-acceptance\"\nversion = \"1.0.0\"\nlicense = { text = \"MIT\" }\n");
        }
        return ms.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string content)
    {
        var entry = zip.CreateEntry(path, CompressionLevel.Optimal);
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
            new { componentId = "live-acceptance-license", name = "Matched license", packagePathOrRange = (string?)"LICENSE",
                sourceUrl = "https://example.invalid/live-acceptance-license", knownVersion = (string?)null, license = "MIT",
                noticeLocations = new[] { "LICENSE" }, modifications = "Original", permissionEvidenceReferences = Array.Empty<string>(),
                origin = "OWN_CONTRIBUTION" }
        }
    };

    private static async Task<string> FindEmail(WebApplicationFactory<Program> factory, string username)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return (await scope.ServiceProvider.GetRequiredService<ApplicationDbContext>().Users.AsNoTracking().SingleAsync(x => x.Username == username)).Email;
    }

    private static async Task<string> Login(HttpClient client, string email)
    {
        using var response = await client.PostAsJsonAsync("/api/auth/login", new { email, password = "Password1!" });
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return json.RootElement.GetProperty("accessToken").GetString()!;
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
    public const string EVIDENCE_PATH_VARIABLE = "ASBLOCK_LIVE_ACCEPTANCE_EVIDENCE_PATH";
    public const string DATA_PROTECTION_KEYS_PATH_VARIABLE = "ASBLOCK_LIVE_ACCEPTANCE_DATA_PROTECTION_PATH";

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
        var error = Assert.Throws<InvalidOperationException>(() =>
            LiveAcceptanceOptions.Resolve(null, null, null, null));

        Assert.Contains("ASBLOCK_LIVE_ACCEPTANCE=1", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_WhenEnabledButConfigurationIsMissing_ShouldFailClearly()
    {
        var missingConfiguration = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        var evidencePath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "evidence.json");

        var error = Assert.Throws<InvalidOperationException>(() =>
            LiveAcceptanceOptions.Resolve("1", missingConfiguration, evidencePath, Path.Combine(Path.GetTempPath(), "assetblock-dataprotection-keys-test")));

        Assert.Contains(LiveAcceptanceOptions.CONFIGURATION_PATH_VARIABLE, error.Message, StringComparison.Ordinal);
        Assert.Contains("existing JSON settings file", error.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(Path.GetDirectoryName(evidencePath)!));
    }
}
