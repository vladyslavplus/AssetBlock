using System.Net;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Ai;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests.Ai;

public sealed class OllamaAiGenerationProviderTests
{
    [Fact]
    public async Task Generate_ShouldVerifyDigestThenSendNonStreamingChat()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (request, _) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Task.FromResult(AiProviderTestFactory.Json(HttpStatusCode.OK, AiProviderTestFactory.OllamaTagsBody()));
                }

                return Task.FromResult(AiProviderTestFactory.Json(HttpStatusCode.OK, """
                    {
                      "model": "fixture-ollama-test",
                      "message": { "content": "{\"title\":\"T\"}" },
                      "prompt_eval_count": 9,
                      "eval_count": 4
                    }
                    """));
            }
        };
        OllamaAiGenerationProvider sut = CreateSut(handler);

        AiGenerationProviderResult result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(2);
        result.Outcome.Should().Be(AiGenerationOutcomeKind.SUCCESS);
        result.ActualModel.Should().Be("fixture-ollama-test");
        result.UpstreamProvider.Should().BeNull();
        result.ModelRevision.Should().Be(AiTestDigests.FIXTURE_DIGEST);
        result.InputTokens.Should().Be(9);
        result.OutputTokens.Should().Be(4);
        handler.LastRequest!.Headers.Authorization.Should().BeNull();
        handler.LastRequest.Method.Should().Be(HttpMethod.Post);
        using var payload = JsonDocument.Parse(handler.LastBody!);
        payload.RootElement.GetProperty("stream").GetBoolean().Should().BeFalse();
        payload.RootElement.GetProperty("model").GetString().Should().Be("fixture-ollama-test");
        payload.RootElement.GetProperty("format").ValueKind.Should().Be(JsonValueKind.Object);
        payload.RootElement.TryGetProperty("digest", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Generate_WhenDigestDoesNotMatchTags_ShouldNotChat()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(
                HttpStatusCode.OK,
                AiProviderTestFactory.OllamaTagsBody(digest: "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")))
        };

        AiGenerationProviderResult result = await CreateSut(handler).Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(1);
        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED);
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_WhenDigestIsMissing_ShouldNotSendHttp()
    {
        var handler = new RecordingHttpMessageHandler();
        OllamaAiGenerationProvider sut = CreateSut(handler, digest: "");

        AiGenerationProviderResult result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(0);
        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED);
        result.ModelRevision.Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, true)]
    [InlineData(HttpStatusCode.BadRequest, ErrorCodes.ERR_AI_INVALID_REQUEST, false)]
    [InlineData(HttpStatusCode.NotFound, ErrorCodes.ERR_AI_INVALID_REQUEST, false)]
    public async Task Generate_ShouldMapTagLookupStatusCodes(HttpStatusCode status, string errorCode, bool retryable)
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(status, """{"error":"model not found raw"}"""))
        };
        var logger = new CollectingLogger<OllamaAiGenerationProvider>();
        OllamaAiGenerationProvider sut = CreateSut(handler, logger: logger);

        AiGenerationProviderResult result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(1);
        result.ErrorCode.Should().Be(errorCode);
        result.IsRetryable.Should().Be(retryable);
        result.ModelRevision.Should().BeNull();
        result.ErrorCode.Should().NotBeNull();
        ErrorCodesToErrorMessages.GetMessage(result.ErrorCode!).Should().NotContain("model not found raw");
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, true)]
    [InlineData(HttpStatusCode.BadRequest, ErrorCodes.ERR_AI_INVALID_REQUEST, false)]
    [InlineData(HttpStatusCode.NotFound, ErrorCodes.ERR_AI_INVALID_REQUEST, false)]
    public async Task Generate_ShouldMapChatStatusCodes(HttpStatusCode status, string errorCode, bool retryable)
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (request, _) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Task.FromResult(AiProviderTestFactory.Json(HttpStatusCode.OK, AiProviderTestFactory.OllamaTagsBody()));
                }

                return Task.FromResult(AiProviderTestFactory.Json(status, """{"error":"model failed raw sk-leak"}"""));
            }
        };
        var logger = new CollectingLogger<OllamaAiGenerationProvider>();
        OllamaAiGenerationProvider sut = CreateSut(handler, logger: logger);

        AiGenerationProviderResult result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(2);
        result.ErrorCode.Should().Be(errorCode);
        result.IsRetryable.Should().Be(retryable);
        result.ModelRevision.Should().BeNull();
        result.ErrorCode.Should().NotBeNull();
        ErrorCodesToErrorMessages.GetMessage(result.ErrorCode!).Should().NotContain("model failed raw");
    }

    [Fact]
    public async Task Generate_WhenCallerCancels_ShouldRethrow()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return AiProviderTestFactory.Json(HttpStatusCode.OK, "{}");
            }
        };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));

        Func<Task<AiGenerationProviderResult>> act = async () => await CreateSut(handler).Generate(AiProviderTestFactory.OllamaRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Generate_WhenStructuredContentMissing_ShouldBeTerminal()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (request, _) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Task.FromResult(AiProviderTestFactory.Json(HttpStatusCode.OK, AiProviderTestFactory.OllamaTagsBody()));
                }

                return Task.FromResult(AiProviderTestFactory.Json(HttpStatusCode.OK, """{"model":"fixture-ollama-test"}"""));
            }
        };

        AiGenerationProviderResult result = await CreateSut(handler).Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_INVALID_RESPONSE);
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_WhenReturnedModelDoesNotMatchConfiguredModel_ShouldBeTerminal()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (request, _) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Task.FromResult(AiProviderTestFactory.Json(HttpStatusCode.OK, AiProviderTestFactory.OllamaTagsBody()));
                }

                return Task.FromResult(AiProviderTestFactory.Json(HttpStatusCode.OK, """
                    {
                      "model": "other-ollama-test",
                      "message": { "content": "{\"title\":\"T\"}" }
                    }
                    """));
            }
        };
        OllamaAiGenerationProvider sut = CreateSut(handler);

        AiGenerationProviderResult result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(2);
        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED);
        result.ActualModel.Should().Be("other-ollama-test");
        result.UpstreamProvider.Should().BeNull();
        result.ModelRevision.Should().BeNull();
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_WhenTagsNearlyExhaustBudgetAndChatHangs_ShouldStayWithinOneTimeout()
    {
        var timeout = TimeSpan.FromMilliseconds(400);
        var tagsElapsedSimulated = TimeSpan.FromMilliseconds(250);
        var recordedTimeouts = new List<TimeSpan>();
        var handler = new RecordingHttpMessageHandler();

        var timedSender = new TimedHttpSender((_, request, reqTimeout, _, _) =>
        {
            recordedTimeouts.Add(reqTimeout);
            if (request.RequestUri?.ToString().Contains("api/tags") == true || request.Method == HttpMethod.Get)
            {
                // Simulate tag lookup time consumption
                Thread.Sleep(tagsElapsedSimulated);
                return Task.FromResult(new AiTimedHttpResult
                {
                    Response = new HttpResponseMessage(HttpStatusCode.OK),
                    Body = AiProviderTestFactory.OllamaTagsBody()
                });
            }

            // Chat request receives the remaining timeout budget and times out
            return Task.FromResult(new AiTimedHttpResult
            {
                TimedOut = true
            });
        });

        OllamaAiGenerationProvider sut = CreateSut(handler, timeout: timeout, timedSender: timedSender);

        AiGenerationProviderResult result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        result.Outcome.Should().Be(AiGenerationOutcomeKind.RETRYABLE_FAILURE);
        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_TIMEOUT);
        result.IsRetryable.Should().BeTrue();

        recordedTimeouts.Should().HaveCount(2);
        recordedTimeouts[0].Should().BeGreaterThan(TimeSpan.Zero);
        recordedTimeouts[0].Should().BeLessThanOrEqualTo(timeout);
        // The second call (chat) must receive the shared remaining budget (<= timeout - tagsElapsed), NOT a fresh timeout!
        recordedTimeouts[1].Should().BeLessThan(timeout);
        recordedTimeouts[1].Should().BeLessThanOrEqualTo(timeout - tagsElapsedSimulated);
    }

    private static OllamaAiGenerationProvider CreateSut(
        RecordingHttpMessageHandler handler,
        string model = "fixture-ollama-test",
        string? digest = null,
        CollectingLogger<OllamaAiGenerationProvider>? logger = null,
        TimeSpan? timeout = null,
        TimedHttpSender? timedSender = null)
    {
        IOptions<OllamaOptions> options = Microsoft.Extensions.Options.Options.Create(new OllamaOptions
        {
            BaseUrl = "http://127.0.0.1:11434",
            Model = model,
            Digest = digest ?? AiTestDigests.FIXTURE_DIGEST,
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            MaxInputChars = 12000,
            MaxOutputTokens = 1000
        });
        IHttpClientFactory factory = AiProviderTestFactory.CreateFactory(
            OllamaAiGenerationProvider.HTTP_CLIENT_NAME,
            handler,
            new Uri("http://127.0.0.1:11434/"));
        return new OllamaAiGenerationProvider(
            factory,
            options,
            logger ?? new CollectingLogger<OllamaAiGenerationProvider>(),
            timedSender ?? AiTimedHttp.Send);
    }
}
