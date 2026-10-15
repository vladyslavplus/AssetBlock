using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Services;

/// <summary>
/// Bounded, metadata-first client for the analysis service. The archive is streamed straight from
/// storage decryption rather than buffered, one deadline token covers the request, the archive copy,
/// the response read and cleanup, and the execution disposition is derived from the verified service
/// envelope rather than from an HTTP status code.
/// </summary>
internal sealed class CodeAnalysisClient(
    HttpClient httpClient,
    IOptions<CodeAnalysisOptions> options,
    ILogger<CodeAnalysisClient> logger) : ICodeAnalysisClient
{
    public const string HTTP_CLIENT_NAME = "code-analysis";

    /// <summary>Schema tag the analysis service stamps on every envelope it produces.</summary>
    private const string SERVICE_SCHEMA = "code-analysis-v1";

    /// <summary>Endpoint path. Fixed by the service contract, never taken from configuration.</summary>
    private const string ANALYSIS_PATH = "/internal/analysis";

    /// <summary>Terminal run outcomes that mean the execution reached an end state.</summary>
    private static readonly HashSet<string> _terminalOutcomes = ["COMPLETED", "FAILED", "CANCELLED"];

    /// <summary>
    /// Pre-acceptance refusals that are deterministic: retrying them unchanged cannot succeed, so they
    /// must not loop through the busy refund path.
    /// </summary>
    private static readonly HashSet<string> _deterministicRefusals =
    [
        "AUTH_MISSING", "AUTH_INVALID", "BODY_TOO_LARGE", "STALE_RUN_CONFLICT",
        "DIGEST_MISMATCH", "POLICY_MISMATCH", "RESERVATION_INVALID", "DEADLINE_EXCEEDED",
        "MALFORMED_METADATA", "MALFORMED_MULTIPART", "UNKNOWN_METADATA_FIELD", "RUN_ID_INVALID"
    ];

    // The service reads the metadata part before the bulk archive, so the request identity is bounded
    // and available before any archive byte is sent.
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly CodeAnalysisOptions _options = options.Value;

    public async Task<CodeAnalysisServiceResponse> Send(
        CodeAnalysisMetadataRequest metadata,
        Func<Stream, CancellationToken, Task> writeArchive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(writeArchive);

        if (!_options.Enabled)
        {
            return new CodeAnalysisServiceResponse(
                AnalysisExecutionDisposition.NOT_STARTED, null, ErrorCodes.ANALYSIS_CAPACITY_UNAVAILABLE, 0);
        }

        var metadataBytes = JsonSerializer.SerializeToUtf8Bytes(metadata, _json);
        if (metadataBytes.Length > _options.MaxMetadataBytes)
        {
            return new CodeAnalysisServiceResponse(
                AnalysisExecutionDisposition.NOT_STARTED, null, ErrorCodes.ANALYSIS_RESPONSE_INVALID, 0);
        }

        // One deadline governs the whole exchange: request serialization, archive copy, response read
        // and cleanup. The per-service deadline is the client-side bound on the wait.
        using var timeoutCts = new CancellationTokenSource(_options.ServiceDeadline);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(_options.BaseUrl), ANALYSIS_PATH));
        request.Headers.TryAddWithoutValidation(_options.AuthenticationHeader, _options.ServiceSecret);

        var content = new MultipartFormDataContent("assetblock-analysis-boundary");
        var metadataPart = new ByteArrayContent(metadataBytes);
        metadataPart.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(metadataPart, "metadata");

        var archivePart = new ArchiveStreamContent(writeArchive, linkedCts.Token);
        archivePart.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(archivePart, "archive", "asset-archive.zip");
        request.Content = content;

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // The request already left this process, so the execution may still be running.
            logger.LogWarning("The analysis request exceeded its service deadline.");
            return Unconfirmed();
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "The analysis request failed at the transport level.");
            return Unconfirmed();
        }

        using (response)
        {
            // Every status is read under the same bounded token; a slow body cannot outlive the deadline.
            var body = await ReadBounded(response, linkedCts.Token).ConfigureAwait(false);
            return Classify(response.StatusCode, body, metadata);
        }
    }

    /// <summary>
    /// Derives the execution disposition from the verified service envelope. An HTTP status alone
    /// proves nothing: the service answers 500 after an unconfirmed containment, and a proxy can
    /// return 200 with a body that is not an envelope at all.
    /// </summary>
    internal static CodeAnalysisServiceResponse Classify(
        HttpStatusCode status,
        string? body,
        CodeAnalysisMetadataRequest metadata)
    {
        var statusCode = (int)status;

        if (body is null
            || !TryParseEnvelope(body, out JsonElement envelope)
            || !TryGetString(envelope, "schema", out var schema)
            || !string.Equals(schema, SERVICE_SCHEMA, StringComparison.Ordinal))
        {
            // Unreadable, oversized, truncated, HTML, or not this service at all.
            return Unconfirmed(statusCode);
        }

        // Execution envelopes always carry an outcome; pre-acceptance refusals never do.
        // Outcome takes precedence so a bound FAILED/CANCELLED execution is never
        // misread as a refusal merely because it also carries a stage errorCode.
        if (TryGetString(envelope, "outcome", out var outcome) && outcome is not null)
        {
            return ClassifyExecution(envelope, outcome, body, status, statusCode, metadata);
        }

        if (TryGetString(envelope, "errorCode", out var errorCode) && errorCode is not null)
        {
            return ClassifyRefusal(envelope, errorCode, statusCode);
        }

        return Unconfirmed(statusCode);
    }

    private static CodeAnalysisServiceResponse ClassifyExecution(
        JsonElement envelope,
        string outcome,
        string body,
        HttpStatusCode status,
        int statusCode,
        CodeAnalysisMetadataRequest metadata)
    {
        // Only the three bound terminal outcomes are executable; anything else is ambiguous.
        if (!_terminalOutcomes.Contains(outcome))
        {
            return Unconfirmed(statusCode);
        }

        var bindingsMatch =
            TryGetString(envelope, "runId", out var runId)
                && string.Equals(runId, metadata.RunId, StringComparison.Ordinal)
            && TryGetString(envelope, "inputDigest", out var inputDigest)
                && string.Equals(inputDigest, metadata.InputDigest, StringComparison.Ordinal)
            && TryGetString(envelope, "contentSha256", out var contentSha256)
                && string.Equals(contentSha256, metadata.ContentSha256, StringComparison.Ordinal);

        if (!bindingsMatch)
        {
            return Unconfirmed(statusCode);
        }

        // Wire schema: cleanupError:null means clean containment; any non-null value
        // means the workspace could not be confirmed cleaned and blocks admission.
        if (IsCleanupFailed(envelope))
        {
            return Unconfirmed(statusCode);
        }

        // SERVICE_BUSY is a pre-acceptance refusal code only. A mixed
        // outcome+SERVICE_BUSY envelope is contradictory and stays fenced.
        if (TryGetString(envelope, "errorCode", out var executionError)
            && string.Equals(executionError, "SERVICE_BUSY", StringComparison.Ordinal))
        {
            return Unconfirmed(statusCode);
        }

        // COMPLETED carries no errorCode; FAILED/CANCELLED carry their stage error.
        // A COMPLETED envelope with an errorCode is contradictory.
        var hasErrorCode = TryGetString(envelope, "errorCode", out _);
        if (string.Equals(outcome, "COMPLETED", StringComparison.Ordinal) && hasErrorCode)
        {
            return Unconfirmed(statusCode);
        }

        if ((string.Equals(outcome, "FAILED", StringComparison.Ordinal)
                || string.Equals(outcome, "CANCELLED", StringComparison.Ordinal))
            && !hasErrorCode)
        {
            return Unconfirmed(statusCode);
        }

        if (status == HttpStatusCode.OK)
        {
            // The execution reached an end state and its containment was confirmed. Whether the
            // report may be admitted is a separate decision made by the finalizer.
            return new CodeAnalysisServiceResponse(
                AnalysisExecutionDisposition.CONFIRMED_TERMINATED, body, null, statusCode);
        }

        // A bound-but-unconfirmed envelope keeps the reservation fenced.
        return Unconfirmed(statusCode);
    }

    private static bool IsCleanupFailed(JsonElement envelope)
    {
        // Wire schema: missing or explicit null means no cleanup error; any non-null
        // value (string code, object, number, true) means containment cleanup failed.
        if (!envelope.TryGetProperty("cleanupError", out JsonElement cleanup))
        {
            return false;
        }

        return cleanup.ValueKind != JsonValueKind.Null && cleanup.ValueKind != JsonValueKind.Undefined;
    }

    private static CodeAnalysisServiceResponse ClassifyRefusal(JsonElement envelope, string errorCode, int statusCode)
    {
        // A refusal proves nothing started only when it has the exact pre-acceptance shape:
        // schema + errorCode, with no outcome and no run binding. Anything carrying a run
        // identity alongside a refusal code is contradictory and stays fenced.
        if (envelope.TryGetProperty("outcome", out _)
            || envelope.TryGetProperty("runId", out _)
            || envelope.TryGetProperty("inputDigest", out _))
        {
            return Unconfirmed(statusCode);
        }
        if (string.Equals(errorCode, "SERVICE_BUSY", StringComparison.Ordinal))
        {
            // The service proved it had not claimed this run, so nothing executed.
            return new CodeAnalysisServiceResponse(
                AnalysisExecutionDisposition.NOT_STARTED, null, ErrorCodes.ANALYSIS_SERVICE_BUSY, statusCode);
        }

        if (_deterministicRefusals.Contains(errorCode))
        {
            // Refused before acceptance, but retrying the same request cannot change the answer.
            return new CodeAnalysisServiceResponse(
                AnalysisExecutionDisposition.NOT_STARTED, null, ErrorCodes.ANALYSIS_RESPONSE_INVALID, statusCode);
        }

        // INTERNAL_ERROR is what the service returns after unconfirmed containment, so it must not be
        // read as either a refusal or a finished execution.
        return Unconfirmed(statusCode);
    }

    private static bool TryParseEnvelope(string body, out JsonElement envelope)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                envelope = default;
                return false;
            }

            envelope = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            envelope = default;
            return false;
        }
    }

    private static bool TryGetString(JsonElement element, string name, out string? value)
    {
        if (element.TryGetProperty(name, out JsonElement property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString();
            return value is not null;
        }

        value = null;
        return false;
    }

    private static CodeAnalysisServiceResponse Unconfirmed(int statusCode = 0) =>
        new(AnalysisExecutionDisposition.UNCONFIRMED, null, ErrorCodes.ANALYSIS_RESPONSE_INVALID, statusCode);

    private async Task<string?> ReadBounded(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];

            while (true)
            {
                var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                if (buffer.Length + read > _options.MaxResponseBytes)
                {
                    return null;
                }

                await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }

            return Encoding.UTF8.GetString(buffer.ToArray());
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            // A body that faults mid-read says nothing about whether the execution ended.
            return null;
        }
    }

    /// <summary>Streams the archive part straight from the decrypted storage pipeline.</summary>
    private sealed class ArchiveStreamContent(Func<Stream, CancellationToken, Task> writeArchive, CancellationToken token)
        : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            var passthrough = new PassthroughStream(stream, leaveOpen: true);
            return writeArchive(passthrough, token);
        }

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            var passthrough = new PassthroughStream(stream, leaveOpen: true);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            await writeArchive(passthrough, linked.Token).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class PassthroughStream(Stream inner, bool leaveOpen) : Stream
    {
        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);

        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(buffer, cancellationToken);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            inner.WriteAsync(buffer, offset, count, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing && !leaveOpen)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
