using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Services;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AssetBlock.WebApi.IntegrationTests;

public sealed class SafetyUploadHttpIntegrationTests : IAsyncLifetime, IDisposable
{
    private readonly IntegrationTestFixture _database = new();
    private IContainer _clamAv = null!;
    private ClamAvTcpProxy _scannerProxy = null!;
    private AssetBlockWebApplicationFactory _factory = null!;

    public async Task InitializeAsync()
    {
        await _database.InitializeAsync();
        _clamAv = new ContainerBuilder(ClamAvTestSupport.IMAGE)
            .WithPortBinding(3310, true)
            .WithEnvironment("CLAMD_CONF_StreamMaxLength", "1M")
            .WithEnvironment("CLAMD_CONF_MaxFileSize", "1M")
            .WithEnvironment("CLAMD_CONF_MaxScanSize", "1M")
            .WithEnvironment("CLAMD_CONF_MaxFiles", "10000")
            .WithEnvironment("CLAMD_CONF_MaxRecursion", "2")
            .WithEnvironment("CLAMD_CONF_AlertExceedsMax", "yes")
            .Build();
        await _clamAv.StartAsync();

        _scannerProxy = new ClamAvTcpProxy("127.0.0.1", _clamAv.GetMappedPublicPort(3310));
        _scannerProxy.Start();
        var port = _scannerProxy.Port;
        var settings = new Dictionary<string, string?>
        {
            ["AssetProcessing:Enabled"] = "true",
            ["AssetProcessing:PollInterval"] = "00:00:01",
            ["AssetProcessing:InitialRetryDelay"] = "00:00:05",
            ["AssetProcessing:MaxRetryDelay"] = "00:01:00",
            ["CodeAnalysis:Enabled"] = "false",
            ["CodeAnalysis:BaseUrl"] = "http://127.0.0.1:8000/",
            ["FileUpload:MaxFileBytes"] = "1048576",
            ["ClamAv:Enabled"] = "true",
            ["ClamAv:Host"] = "127.0.0.1",
            ["ClamAv:Port"] = port.ToString(),
            ["ClamAv:VersionTimeZoneId"] = "UTC",
            ["ClamAv:MaxStreamBytes"] = "1048576",
            ["ClamAv:DaemonMaxStreamBytes"] = "1048576",
            ["ClamAv:ConnectTimeoutMs"] = "500",
            ["ClamAv:ReadTimeoutMs"] = "1000",
            ["ClamAv:WriteTimeoutMs"] = "1000"
        };
        _factory = new AssetBlockWebApplicationFactory(_database.ConnectionString, settings);
    }

    public async Task DisposeAsync()
    {
        if (_factory is not null)
        {
            await _factory.DisposeAsync();
        }
        if (_scannerProxy is not null)
        {
            await _scannerProxy.DisposeAsync();
        }
        if (_clamAv is not null)
        {
            await _clamAv.DisposeAsync();
        }
        await _database.DisposeAsync();
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _scannerProxy?.Dispose();
        _database.Dispose();
    }

    [Fact]
    public async Task UploadUnsafeArchiveAndMalwareThroughHttp_ShouldRejectAndBlockDownstreamActions()
    {
        int port = _clamAv.GetMappedPublicPort(3310);
        await WaitForClamAv(port, TimeSpan.FromMinutes(5));
        (HttpClient seller, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(_factory);
        using (seller)
        {
            Guid categoryId = await CreateCategory();

            UploadResult unsafeUpload = await Upload(seller, categoryId, "unsafe-path.zip", CreateZip("../outside.txt", "unsafe"));
            JsonElement unsafeReview = await WaitForReview(seller, unsafeUpload.VersionId, "REJECTED");
            AssertRejectedReview(unsafeReview, "ARCHIVE_PATH_TRAVERSAL");
            await AssertBlockedActions(seller, unsafeUpload);
            await AssertNoDownstreamJobs(unsafeUpload.VersionId, expectMalwareScan: false);

            _scannerProxy.ForwardingEnabled = false;
            UploadResult infectedUpload = await Upload(seller, categoryId, "runtime-scan.zip", CreateZip("src/runtime.txt", ClamAvTestSupport.CreateRuntimeEicarPayload()));
            await WaitForJob(infectedUpload.VersionId, AssetProcessingJobType.MALWARE_SCAN, AssetProcessingJobStatus.RETRY_SCHEDULED);
            JsonElement retryReview = await GetReview(seller, infectedUpload.VersionId);
            retryReview.GetProperty("processingStatus").GetString().Should().Be("PENDING_MALWARE_SCAN");
            retryReview.GetProperty("canSubmit").GetBoolean().Should().BeFalse();

            _scannerProxy.ForwardingEnabled = true;
            JsonElement infectedReview = await WaitForReview(seller, infectedUpload.VersionId, "REJECTED");
            AssertRejectedReview(infectedReview, "MALWARE_DETECTED");
            await AssertBlockedActions(seller, infectedUpload);
            await AssertNoDownstreamJobs(infectedUpload.VersionId, expectMalwareScan: true);
        }
    }

    private async Task<Guid> CreateCategory()
    {
        var id = Guid.NewGuid();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        db.Categories.Add(new Category { Id = id, Name = "Safety integration", Slug = "safety-" + id.ToString("N") });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<UploadResult> Upload(HttpClient seller, Guid categoryId, string fileName, byte[] archive)
    {
        using HttpResponseMessage created = await seller.PostAsJsonAsync("/" + ApiRoutes.Assets.BASE + "/" + ApiRoutes.Assets.DRAFT_CREATE, new
        {
            operationId = Guid.NewGuid(),
            title = "Safety check upload",
            description = "Test-only isolated safety check",
            price = 1.25m,
            categoryId,
            downloadLimitPerHour = 10
        });
        created.EnsureSuccessStatusCode();
        using var createdBody = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Guid assetId = createdBody.RootElement.GetProperty("assetId").GetGuid();
        Guid workspaceId = createdBody.RootElement.GetProperty("workspaceId").GetGuid();
        var revision = createdBody.RootElement.GetProperty("workspaceRevision").GetInt64();

        using HttpResponseMessage declared = await seller.PutAsJsonAsync(AssetPath(ApiRoutes.Assets.DECLARATION, assetId), new
        {
            operationId = Guid.NewGuid(),
            expectedWorkspaceRevision = revision,
            declaration = AuthenticatedEncryptedUploadAcceptanceTests.BuildDeclaration()
        });
        declared.EnsureSuccessStatusCode();
        using var declaredBody = JsonDocument.Parse(await declared.Content.ReadAsStringAsync());
        revision = declaredBody.RootElement.GetProperty("workspaceRevision").GetInt64();

        using var multipart = new MultipartFormDataContent();
        multipart.Add(new StringContent("PERSONAL"), "LicenseCode");
        multipart.Add(new StringContent("Safety test upload"), "ReleaseNotes");
        multipart.Add(new StringContent(workspaceId.ToString()), "WorkspaceId");
        multipart.Add(new StringContent(revision.ToString()), "ExpectedWorkspaceRevision");
        var payload = new ByteArrayContent(archive);
        payload.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        multipart.Add(payload, "file", fileName);
        using HttpResponseMessage uploaded = await seller.PostAsync(AssetPath(ApiRoutes.Assets.VERSIONS, assetId), multipart);
        uploaded.EnsureSuccessStatusCode();
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        AssetVersion version = await db.AssetVersions.AsNoTracking().SingleAsync(item => item.AssetId == assetId);
        AssetDraftWorkspace workspace = await db.AssetDraftWorkspaces.AsNoTracking().SingleAsync(item => item.AssetVersionId == version.Id);
        return new UploadResult(assetId, version.Id, workspace.Id, workspace.WorkspaceRevision, workspace.CaseRevision);
    }

    private static string AssetPath(string route, Guid assetId, Guid? versionId = null) =>
        "/" + ApiRoutes.Assets.BASE + "/" + route.Replace("{id:guid}", assetId.ToString(), StringComparison.Ordinal)
            .Replace("{versionId:guid}", versionId?.ToString() ?? "", StringComparison.Ordinal);

    private static async Task<JsonElement> WaitForReview(HttpClient seller, Guid versionId, string status)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        JsonElement latest = default;
        while (DateTime.UtcNow < deadline)
        {
            latest = await GetReview(seller, versionId);
            if (latest.GetProperty("processingStatus").GetString() == status)
            {
                return latest;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Upload did not reach {status}; last status was {latest.GetProperty("processingStatus").GetString()}.");
    }

    private static async Task<JsonElement> GetReview(HttpClient seller, Guid versionId)
    {
        using HttpResponseMessage response = await seller.GetAsync(AuthenticatedEncryptedUploadAcceptanceTests.SellerVersionReviewUrl(versionId));
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    private static void AssertRejectedReview(JsonElement review, string code)
    {
        review.GetProperty("processingStatus").GetString().Should().Be("REJECTED");
        review.GetProperty("processingErrorCode").GetString().Should().Be(code);
        var reason = review.GetProperty("processingErrorSummary").GetString()!;
        reason.Should().NotBeNullOrWhiteSpace();
        reason.Should().MatchRegex("^[A-Za-z]");
        review.GetProperty("canSubmit").GetBoolean().Should().BeFalse();
        review.GetProperty("publicationEligible").GetBoolean().Should().BeFalse();
    }

    private async Task AssertBlockedActions(HttpClient seller, UploadResult upload)
    {
        using HttpResponseMessage download = await seller.GetAsync(AssetPath(ApiRoutes.Assets.VERSION_DOWNLOAD, upload.AssetId, upload.VersionId));
        download.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        AssetDraftWorkspace currentWorkspace = await db.AssetDraftWorkspaces.AsNoTracking()
            .SingleAsync(workspace => workspace.Id == upload.WorkspaceId && workspace.AssetId == upload.AssetId);
        currentWorkspace.AssetVersionId.Should().Be(upload.VersionId);
        currentWorkspace.WorkspaceRevision.Should().Be(upload.Revision);
        currentWorkspace.CaseRevision.Should().Be(upload.CaseRevision);

        // CodeAnalysis is disabled for this host; this checks the no-analysis submission prerequisite, not a safety-specific override.
        using HttpResponseMessage submit = await seller.PostAsJsonAsync(AssetPath(ApiRoutes.Assets.VERSION_SUBMISSIONS, upload.AssetId, upload.VersionId), new
        {
            workspaceId = upload.WorkspaceId,
            expectedWorkspaceRevision = upload.Revision,
            expectedCaseRevision = upload.CaseRevision,
            operationId = Guid.NewGuid()
        });
        submit.StatusCode.Should().Be(HttpStatusCode.Conflict);
        using var submissionProblem = JsonDocument.Parse(await submit.Content.ReadAsStringAsync());
        submissionProblem.RootElement.GetProperty("code").GetString().Should().Be(ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE);

        Guid? publicationSnapshotId = await db.Assets.AsNoTracking().Where(asset => asset.Id == upload.AssetId)
            .Select(asset => asset.CurrentPublicationSnapshotId).SingleAsync();
        publicationSnapshotId.Should().BeNull();
        var hasModerationSubmission = await db.ModerationSubmissions.AsNoTracking()
            .AnyAsync(submission => submission.AssetVersionId == upload.VersionId);
        hasModerationSubmission.Should().BeFalse();
    }

    private async Task AssertNoDownstreamJobs(Guid versionId, bool expectMalwareScan)
    {
        await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        List<AssetProcessingJobType> types = await db.AssetProcessingJobs.AsNoTracking()
            .Where(job => job.AssetVersionId == versionId).Select(job => job.Type).ToListAsync();
        types.Should().NotContain(AssetProcessingJobType.CODE_ANALYSIS);
        if (!expectMalwareScan)
        {
            types.Should().NotContain(AssetProcessingJobType.MALWARE_SCAN);
        }
    }

    private async Task WaitForJob(Guid versionId, AssetProcessingJobType type, AssetProcessingJobStatus status)
    {
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            await using AsyncServiceScope scope = _factory.Services.CreateAsyncScope();
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            if (await db.AssetProcessingJobs.AsNoTracking().AnyAsync(job => job.AssetVersionId == versionId && job.Type == type && job.Status == status))
            {
                return;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Processing job {type} did not reach {status}.");
    }

    private static byte[] CreateZip(string path, string content)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry(path);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(content);
        }
        return output.ToArray();
    }

    private static byte[] CreateZip(string path, byte[] content)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = zip.CreateEntry(path);
            using Stream stream = entry.Open();
            stream.Write(content);
        }
        return output.ToArray();
    }

    private static async Task WaitForClamAv(int port, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        var scanner = new ClamAvContentMalwareScanner(
            Options.Create(new ClamAvOptions { Enabled = true, Host = "127.0.0.1", Port = port, VersionTimeZoneId = "UTC", MaxStreamBytes = 1024 * 1024, DaemonMaxStreamBytes = 1024 * 1024 }),
            NullLogger<ClamAvContentMalwareScanner>.Instance);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if ((await scanner.GetSignatureState()).IsAvailable)
                {
                    return;
                }
            }
            catch
            {
                // Wait while clamd loads signatures or restarts.
            }
            await Task.Delay(500);
        }
        throw new TimeoutException("ClamAV TCP listener did not become available.");
    }

    private sealed record UploadResult(Guid AssetId, Guid VersionId, Guid WorkspaceId, long Revision, long CaseRevision);
}

internal sealed class ClamAvTcpProxy(string targetHost, int targetPort, Func<Task>? beforeRelayCompletion = null) : IDisposable, IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _relayTasksSync = new();
    private readonly HashSet<Task> _relayTasks = [];
    private readonly object _disposeSync = new();
    private readonly Func<Task>? _beforeRelayCompletion = beforeRelayCompletion;
    private Task? _acceptTask;
    private Task? _disposeTask;
    private int _forwardingEnabled = 1;
    private int _disposed;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    internal Task AcceptLoopCompletion => _acceptTask ?? Task.CompletedTask;

    internal bool AcceptLoopCompleted => _acceptTask?.IsCompleted == true;

    internal int IncompleteRelayCount
    {
        get
        {
            lock (_relayTasksSync)
            {
                return _relayTasks.Count(task => !task.IsCompleted);
            }
        }
    }

    internal int TrackedRelayCount
    {
        get
        {
            lock (_relayTasksSync)
            {
                return _relayTasks.Count;
            }
        }
    }

    public bool ForwardingEnabled
    {
        get => Volatile.Read(ref _forwardingEnabled) == 1;
        set => Volatile.Write(ref _forwardingEnabled, value ? 1 : 0);
    }

    public void Start()
    {
        _listener.Start();
        _acceptTask = AcceptConnectionsAsync(_stop.Token);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _stop.Cancel();
        _listener.Stop();
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposeSync)
        {
            _disposeTask ??= DisposeCoreAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Dispose();
        Exception? acceptFailure = null;
        try
        {
            if (_acceptTask is not null)
            {
                await _acceptTask;
            }
        }
        catch (Exception exception)
        {
            acceptFailure = exception;
        }

        Task[] relayTasks;
        lock (_relayTasksSync)
        {
            relayTasks = _relayTasks.ToArray();
        }

        Exception? relayFailure = null;
        try
        {
            await Task.WhenAll(relayTasks);
        }
        catch (Exception exception)
        {
            relayFailure = exception;
        }
        finally
        {
            _stop.Dispose();
        }

        if (acceptFailure is not null && relayFailure is not null)
        {
            throw new AggregateException("The ClamAV proxy accept loop and relay tasks failed during disposal.", acceptFailure, relayFailure);
        }
        if (acceptFailure is not null)
        {
            ExceptionDispatchInfo.Capture(acceptFailure).Throw();
        }
        if (relayFailure is not null)
        {
            ExceptionDispatchInfo.Capture(relayFailure).Throw();
        }
    }

    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            Task relay = RelayAsync(client, cancellationToken);
            lock (_relayTasksSync)
            {
                _relayTasks.Add(relay);
            }
        }
    }

    private async Task RelayAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            if (!ForwardingEnabled)
            {
                return;
            }

            try
            {
                using var upstream = new TcpClient();
                await upstream.ConnectAsync(targetHost, targetPort, cancellationToken);
                using var relayCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                Task upload = client.GetStream().CopyToAsync(upstream.GetStream(), relayCancellation.Token);
                Task download = upstream.GetStream().CopyToAsync(client.GetStream(), relayCancellation.Token);
                await Task.WhenAny(upload, download);
                relayCancellation.Cancel();
                await Task.WhenAll(ObserveExpectedRelayFailure(upload), ObserveExpectedRelayFailure(download));
                if (_beforeRelayCompletion is not null)
                {
                    await _beforeRelayCompletion();
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private static async Task ObserveExpectedRelayFailure(Task relay)
    {
        try
        {
            await relay;
        }
        catch (IOException)
        {
        }
        catch (SocketException)
        {
        }
        catch (OperationCanceledException)
        {
        }
    }
}
