using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace AssetBlock.Infrastructure.Ai;

internal static class AiHttpStatusClassifier
{
    public static bool IsRetryable(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}

internal static class BoundedHttpContentReader
{
    public static async Task<string?> ReadUtf8(
        HttpResponseMessage response,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } length && length > maxBytes)
        {
            return null;
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 4096));
        var chunk = new byte[4096];
        var total = 0;
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maxBytes)
            {
                return null;
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}

internal static class RetryAfterParser
{
    public static TimeSpan? Parse(HttpResponseHeaders headers, TimeSpan maxRetryAfter, TimeProvider? timeProvider = null)
    {
        if (headers.RetryAfter is null)
        {
            return null;
        }

        TimeSpan delay;
        if (headers.RetryAfter.Delta is { } delta)
        {
            delay = delta;
        }
        else if (headers.RetryAfter.Date is { } date)
        {
            delay = date - (timeProvider ?? TimeProvider.System).GetUtcNow();
        }
        else
        {
            return null;
        }

        if (delay < TimeSpan.Zero)
        {
            delay = TimeSpan.Zero;
        }

        return delay > maxRetryAfter ? maxRetryAfter : delay;
    }
}

internal static class AiHttpExceptionMapping
{
    public static bool IsCallerCancellation(OperationCanceledException exception, CancellationToken cancellationToken) =>
        cancellationToken.IsCancellationRequested
        || (exception is TaskCanceledException { CancellationToken.IsCancellationRequested: true } && cancellationToken.IsCancellationRequested);
}

internal sealed class AiTimedHttpResult : IDisposable
{
    public HttpResponseMessage? Response { get; init; }
    public string? Body { get; init; }
    public bool TimedOut { get; init; }
    public bool NetworkFailure { get; init; }
    public bool Oversized { get; init; }

    public void Dispose() => Response?.Dispose();
}

internal static class AiTimeoutBudget
{
    public static TimeSpan Remaining(TimeSpan budget, TimeSpan elapsed)
    {
        TimeSpan left = budget - elapsed;
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }
}

internal delegate Task<AiTimedHttpResult> TimedHttpSender(
    HttpClient client,
    HttpRequestMessage request,
    TimeSpan timeout,
    int maxResponseBytes,
    CancellationToken callerToken);

internal static class AiTimedHttp
{
    public static async Task<AiTimedHttpResult> Send(
        HttpClient client,
        HttpRequestMessage request,
        TimeSpan timeout,
        int maxResponseBytes,
        CancellationToken callerToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        linkedCts.CancelAfter(timeout);
        HttpResponseMessage? response = null;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linkedCts.Token);
            var body = await BoundedHttpContentReader.ReadUtf8(response, maxResponseBytes, linkedCts.Token);
            if (body is null)
            {
                return new AiTimedHttpResult { Response = response, Oversized = true };
            }

            return new AiTimedHttpResult { Response = response, Body = body };
        }
        catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
        {
            response?.Dispose();
            throw;
        }
        catch (OperationCanceledException)
        {
            response?.Dispose();
            return new AiTimedHttpResult { TimedOut = true };
        }
        catch (HttpRequestException)
        {
            response?.Dispose();
            return new AiTimedHttpResult { NetworkFailure = true };
        }
        catch (IOException)
        {
            response?.Dispose();
            return new AiTimedHttpResult { NetworkFailure = true };
        }
    }
}
