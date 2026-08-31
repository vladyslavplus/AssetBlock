using System.Net;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Tests.Ai;

internal sealed class RecordingHttpMessageHandler : HttpMessageHandler
{
    public int SendCount { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }
    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Responder { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        SendCount++;
        LastRequest = request;
        LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        return await Responder(request, cancellationToken);
    }
}

internal static class AiTestDigests
{
    public const string FIXTURE_DIGEST = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
}

internal sealed class CollectingLogger<T> : ILogger<T>
{
    public List<string> Messages { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        Messages.Add(formatter(state, exception));
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();
        public void Dispose()
        {
        }
    }
}

internal static class AiProviderTestFactory
{
    public static IHttpClientFactory CreateFactory(string name, RecordingHttpMessageHandler handler, Uri baseAddress)
    {
        var services = new ServiceCollection();
        services.AddHttpClient(name)
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .ConfigureHttpClient(client =>
            {
                client.BaseAddress = baseAddress;
                client.Timeout = Timeout.InfiniteTimeSpan;
            });
        return services.BuildServiceProvider().GetRequiredService<IHttpClientFactory>();
    }

    public static AiGenerationRequest OpenRouterRequest() =>
        new(
            AiProviderKind.OPENROUTER,
            AiPromptPolicies.LISTING_COPILOT_V1,
            "system",
            "user",
            """{"type":"object"}""",
            100);

    public static AiGenerationRequest OllamaRequest() =>
        new(
            AiProviderKind.OLLAMA,
            AiPromptPolicies.LISTING_COPILOT_V1,
            "system",
            "user",
            """{"type":"object"}""",
            100);

    public static HttpResponseMessage Json(HttpStatusCode status, string json)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    public static string OpenRouterSuccessBody(
        string model = "fixture/openrouter-test",
        string content = """{"title":"T"}""",
        object? availableEndpoints = null)
    {
        return JsonSerializer.Serialize(new
        {
            id = "gen-123",
            model,
            usage = new { prompt_tokens = 11, completion_tokens = 7 },
            openrouter_metadata = new
            {
                endpoints = new
                {
                    available = availableEndpoints ?? new object[]
                    {
                        new { name = "TestHost", selected = true }
                    }
                }
            },
            choices = new[] { new { message = new { content } } }
        });
    }

    public static string OllamaTagsBody(string model = "fixture-ollama-test", string? digest = null) =>
        JsonSerializer.Serialize(new
        {
            models = new[]
            {
                new { name = model, digest = digest ?? AiTestDigests.FIXTURE_DIGEST }
            }
        });
}

internal sealed class HangingReadStream : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get; set; }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException();

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.Infinite, cancellationToken);
        return 0;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
