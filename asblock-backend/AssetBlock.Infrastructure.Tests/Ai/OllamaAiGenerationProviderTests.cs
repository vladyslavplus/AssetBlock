using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Ai;

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
        var sut = CreateSut(handler);

        var result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(2);
        result.Outcome.Should().Be(AiGenerationOutcomeKind.SUCCESS);
        result.ActualModel.Should().Be("fixture-ollama-test");
        result.UpstreamProvider.Should().BeNull();
        result.ModelRevision.Should().Be(StaticAiModelPolicyCatalog.FixtureDigest);
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

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(1);
        result.ErrorCode.Should().Be(ErrorCodes.AI_MODEL_NOT_ALLOWED);
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_WhenModelMissingFromPolicy_ShouldNotSendHttp()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSut(handler, model: "missing-model", catalog: new StaticAiModelPolicyCatalog());

        var result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(0);
        result.ErrorCode.Should().Be(ErrorCodes.AI_MODEL_NOT_ALLOWED);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCodes.AI_PROVIDER_UNAVAILABLE, true)]
    [InlineData(HttpStatusCode.BadRequest, ErrorCodes.AI_INVALID_REQUEST, false)]
    [InlineData(HttpStatusCode.NotFound, ErrorCodes.AI_INVALID_REQUEST, false)]
    public async Task Generate_ShouldMapTagLookupStatusCodes(HttpStatusCode status, string errorCode, bool retryable)
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(status, """{"error":"model not found raw"}"""))
        };
        var logger = new CollectingLogger<OllamaAiGenerationProvider>();
        var sut = CreateSut(handler, logger: logger);

        var result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(1);
        result.ErrorCode.Should().Be(errorCode);
        result.IsRetryable.Should().Be(retryable);
        logger.Messages.Should().NotContain(m => m.Contains("model not found raw"));
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

        var act = async () => await CreateSut(handler).Generate(AiProviderTestFactory.OllamaRequest(), cts.Token);

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

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.AI_INVALID_RESPONSE);
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_WhenReturnedModelIsInPolicyButNotRequested_ShouldBeTerminal()
    {
        var otherDigest = "sha256:bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
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
        var catalog = new StaticAiModelPolicyCatalog(
            StaticAiModelPolicyCatalog.OllamaFixture(),
            StaticAiModelPolicyCatalog.OllamaFixture() with
            {
                ModelId = "other-ollama-test",
                Digest = otherDigest
            });
        var sut = CreateSut(handler, catalog: catalog);

        var result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(2);
        result.ErrorCode.Should().Be(ErrorCodes.AI_MODEL_NOT_ALLOWED);
        result.ActualModel.Should().Be("other-ollama-test");
        result.UpstreamProvider.Should().BeNull();
        result.ModelRevision.Should().BeNull();
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_WhenTagsNearlyExhaustBudgetAndChatHangs_ShouldStayWithinOneTimeout()
    {
        var timeout = TimeSpan.FromMilliseconds(400);
        var tagsDelay = TimeSpan.FromMilliseconds(250);
        var handler = new RecordingHttpMessageHandler
        {
            Responder = async (request, ct) =>
            {
                if (request.Method == HttpMethod.Get)
                {
                    await Task.Delay(tagsDelay, ct);
                    return AiProviderTestFactory.Json(HttpStatusCode.OK, AiProviderTestFactory.OllamaTagsBody());
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(new HangingReadStream())
                };
            }
        };
        var sut = CreateSut(handler, timeout: timeout);
        var elapsed = Stopwatch.StartNew();

        var result = await sut.Generate(AiProviderTestFactory.OllamaRequest(), CancellationToken.None);

        elapsed.Stop();
        result.ErrorCode.Should().Be(ErrorCodes.AI_TIMEOUT);
        result.IsRetryable.Should().BeTrue();
        elapsed.Elapsed.Should().BeGreaterThan(tagsDelay);
        // Shared remaining budget ~400ms. A fresh chat Timeout would be ~650ms+.
        elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromMilliseconds(520));
    }

    private static OllamaAiGenerationProvider CreateSut(
        RecordingHttpMessageHandler handler,
        string model = "fixture-ollama-test",
        StaticAiModelPolicyCatalog? catalog = null,
        CollectingLogger<OllamaAiGenerationProvider>? logger = null,
        TimeSpan? timeout = null)
    {
        catalog ??= new StaticAiModelPolicyCatalog(StaticAiModelPolicyCatalog.OllamaFixture());
        var options = Microsoft.Extensions.Options.Options.Create(new OllamaOptions
        {
            BaseUrl = "http://127.0.0.1:11434",
            Model = model,
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            MaxInputChars = 12000,
            MaxOutputTokens = 1000
        });
        var factory = AiProviderTestFactory.CreateFactory(
            OllamaAiGenerationProvider.HTTP_CLIENT_NAME,
            handler,
            new Uri("http://127.0.0.1:11434/"));
        return new OllamaAiGenerationProvider(
            factory,
            options,
            catalog,
            logger ?? new CollectingLogger<OllamaAiGenerationProvider>());
    }
}
