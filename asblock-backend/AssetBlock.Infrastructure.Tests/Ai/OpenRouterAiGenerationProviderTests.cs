using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Ai;

namespace AssetBlock.Infrastructure.Tests.Ai;

public sealed class OpenRouterAiGenerationProviderTests
{
    private const string API_KEY = "sk-test-openrouter-secret";

    [Fact]
    public async Task Generate_ShouldSendOneRequestWithOrderedModelsAndSafeHeaders()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(HttpStatusCode.OK, AiProviderTestFactory.OpenRouterSuccessBody()))
        };
        var sut = CreateSut(handler, ["fixture/openrouter-test", "fixture/openrouter-test-b"]);

        var result = await sut.Generate(
            AiProviderTestFactory.OpenRouterRequest(),
            CancellationToken.None);

        handler.SendCount.Should().Be(1);
        result.Outcome.Should().Be(AiGenerationOutcomeKind.SUCCESS);
        result.ActualModel.Should().Be("fixture/openrouter-test");
        result.UpstreamProvider.Should().Be("TestHost");
        result.ModelRevision.Should().BeNull();
        result.RequestId.Should().Be("gen-123");
        result.InputTokens.Should().Be(11);
        result.OutputTokens.Should().Be(7);
        handler.LastRequest!.Headers.Authorization!.ToString().Should().Be($"Bearer {API_KEY}");
        handler.LastRequest.Headers.GetValues("HTTP-Referer").Single().Should().Be("https://example.test/");
        handler.LastRequest.Headers.GetValues("X-OpenRouter-Title").Single().Should().Be("AssetBlock");
        handler.LastRequest.Headers.GetValues("X-OpenRouter-Metadata").Single().Should().Be("enabled");
        using var payload = JsonDocument.Parse(handler.LastBody!);
        payload.RootElement.GetProperty("models").EnumerateArray().Select(e => e.GetString()).Should()
            .Equal("fixture/openrouter-test", "fixture/openrouter-test-b");
        payload.RootElement.TryGetProperty("model", out _).Should().BeFalse();
        payload.RootElement.GetProperty("provider").GetProperty("require_parameters").GetBoolean().Should().BeTrue();
        payload.RootElement.GetProperty("provider").GetProperty("data_collection").GetString().Should().Be("deny");
        payload.RootElement.GetProperty("provider").TryGetProperty("zdr", out _).Should().BeFalse();
        payload.RootElement.GetProperty("response_format").GetProperty("json_schema").GetProperty("strict").GetBoolean()
            .Should().BeTrue();
        handler.LastBody.Should().NotContain(API_KEY);
    }

    [Fact]
    public async Task Generate_WhenConfiguredModelsAreEmpty_ShouldNotSendHttp()
    {
        var handler = new RecordingHttpMessageHandler();
        var sut = CreateSut(handler, []);

        var result = await sut.Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(0);
        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED);
        result.IsRetryable.Should().BeFalse();
        result.ModelRevision.Should().BeNull();
        result.ErrorCode.Should().NotBeNull();
        ErrorCodesToErrorMessages.GetMessage(result.ErrorCode!).Should().NotContain("unapproved/model");
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout, ErrorCodes.ERR_AI_TIMEOUT, true)]
    [InlineData(HttpStatusCode.TooManyRequests, ErrorCodes.ERR_AI_RATE_LIMITED, true)]
    [InlineData(HttpStatusCode.InternalServerError, ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, true)]
    [InlineData(HttpStatusCode.BadGateway, ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, true)]
    [InlineData(HttpStatusCode.Unauthorized, ErrorCodes.ERR_AI_UNAUTHORIZED, false)]
    [InlineData(HttpStatusCode.PaymentRequired, ErrorCodes.ERR_AI_PAYMENT_REQUIRED, false)]
    [InlineData(HttpStatusCode.Forbidden, ErrorCodes.ERR_AI_FORBIDDEN, false)]
    [InlineData(HttpStatusCode.BadRequest, ErrorCodes.ERR_AI_INVALID_REQUEST, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, ErrorCodes.ERR_AI_INVALID_REQUEST, false)]
    public async Task Generate_ShouldMapStatusCodes(HttpStatusCode status, string errorCode, bool retryable)
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(status, """{"error":"raw secret body sk-leak"}"""))
        };
        var logger = new CollectingLogger<OpenRouterAiGenerationProvider>();
        var sut = CreateSut(handler, logger: logger);

        var result = await sut.Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(errorCode);
        result.IsRetryable.Should().Be(retryable);
        result.StructuredJson.Should().BeNull();
        logger.Messages.Should().NotContain(m => m.Contains("sk-leak") || m.Contains(API_KEY));
        ErrorCodesToErrorMessages.GetMessage(errorCode).Should().NotContain("sk-leak");
    }

    [Fact]
    public async Task Generate_WhenRetryAfterIsSeconds_ShouldHonorDelay()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) =>
            {
                var response = AiProviderTestFactory.Json(HttpStatusCode.TooManyRequests, "{}");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return Task.FromResult(response);
            }
        };

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(120));
        result.IsRetryable.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_WhenRetryAfterIsHttpDate_ShouldHonorDelay()
    {
        var when = DateTimeOffset.UtcNow.AddMinutes(4);
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) =>
            {
                var response = AiProviderTestFactory.Json(HttpStatusCode.TooManyRequests, "{}");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(when);
                return Task.FromResult(response);
            }
        };

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.RetryAfter.Should().NotBeNull();
        result.RetryAfter!.Value.Should().BeGreaterThan(TimeSpan.FromMinutes(3));
        result.RetryAfter.Value.Should().BeLessThan(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task Generate_WhenRetryAfterExceedsMax_ShouldClamp()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) =>
            {
                var response = AiProviderTestFactory.Json(HttpStatusCode.TooManyRequests, "{}");
                response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(10));
                return Task.FromResult(response);
            }
        };

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.RetryAfter.Should().Be(TimeSpan.FromHours(1));
    }

    [Fact]
    public async Task Generate_WhenNetworkFails_ShouldBeRetryable()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => throw new HttpRequestException("connection reset")
        };

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE);
        result.IsRetryable.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_WhenInternalTimeout_ShouldBeRetryable()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = async (_, ct) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return AiProviderTestFactory.Json(HttpStatusCode.OK, "{}");
            }
        };
        var sut = CreateSut(handler, timeout: TimeSpan.FromMilliseconds(50));

        var result = await sut.Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_TIMEOUT);
        result.IsRetryable.Should().BeTrue();
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
        var sut = CreateSut(handler);

        var act = async () => await sut.Generate(AiProviderTestFactory.OpenRouterRequest(), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Generate_WhenResponseIsMalformed_ShouldBeTerminal()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(HttpStatusCode.OK, "not-json"))
        };

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_INVALID_RESPONSE);
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_WhenResponseExceedsBound_ShouldBeTerminal()
    {
        var oversized = new string('a', OpenRouterOptions.MAX_RESPONSE_BYTES + 8);
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{" + oversized, Encoding.UTF8, "application/json")
            })
        };

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_INVALID_RESPONSE);
    }

    [Fact]
    public async Task Generate_WhenReturnedModelIsNotAllowlisted_ShouldBeTerminal()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(
                HttpStatusCode.OK,
                AiProviderTestFactory.OpenRouterSuccessBody(model: "unexpected/model")))
        };

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED);
        result.ActualModel.Should().Be("unexpected/model");
        result.UpstreamProvider.Should().Be("TestHost");
        result.ModelRevision.Should().BeNull();
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_WhenReturnedModelIsFallbackInConfiguredList_ShouldSucceed()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(
                HttpStatusCode.OK,
                AiProviderTestFactory.OpenRouterSuccessBody(model: "fixture/openrouter-test-b")))
        };
        var sut = CreateSut(handler, ["fixture/openrouter-test", "fixture/openrouter-test-b"]);

        var result = await sut.Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.Outcome.Should().Be(AiGenerationOutcomeKind.SUCCESS);
        result.ActualModel.Should().Be("fixture/openrouter-test-b");
        result.ModelRevision.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public async Task Generate_WhenReturnedModelIsOutsideConfiguredList_ShouldBeTerminal()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(
                HttpStatusCode.OK,
                AiProviderTestFactory.OpenRouterSuccessBody(model: "fixture/openrouter-test-b")))
        };
        var sut = CreateSut(handler, ["fixture/openrouter-test"]);

        var result = await sut.Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED);
        result.ActualModel.Should().Be("fixture/openrouter-test-b");
        result.ModelRevision.Should().BeNull();
        result.IsRetryable.Should().BeFalse();
    }

    [Fact]
    public async Task Generate_ShouldReadSelectedUpstreamProviderNotFirstCandidate()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(
                HttpStatusCode.OK,
                AiProviderTestFactory.OpenRouterSuccessBody(
                    availableEndpoints: new object[]
                    {
                        new { name = "WrongHost", selected = false },
                        new { name = "RightHost", selected = true }
                    })))
        };

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.Outcome.Should().Be(AiGenerationOutcomeKind.SUCCESS);
        result.UpstreamProvider.Should().Be("RightHost");
        result.ModelRevision.Should().BeNull();
    }

    [Fact]
    public async Task Generate_WhenNoEndpointIsSelected_ShouldLeaveUpstreamProviderNull()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(
                HttpStatusCode.OK,
                AiProviderTestFactory.OpenRouterSuccessBody(
                    availableEndpoints: new object[]
                    {
                        new { name = "WrongHost", selected = false }
                    })))
        };

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.Outcome.Should().Be(AiGenerationOutcomeKind.SUCCESS);
        result.UpstreamProvider.Should().BeNull();
    }

    [Fact]
    public async Task Generate_WhenBodyHangsAfterHeaders_ShouldTimeOutRetryably()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new HangingReadStream())
            })
        };
        var sut = CreateSut(handler, timeout: TimeSpan.FromMilliseconds(80));

        var result = await sut.Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        handler.SendCount.Should().Be(1);
        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_TIMEOUT);
        result.IsRetryable.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_WhenBodyDisconnects_ShouldBeRetryable()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => throw new IOException("connection reset while reading body")
        };

        var result = await CreateSut(handler).Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        result.ErrorCode.Should().Be(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE);
        result.IsRetryable.Should().BeTrue();
    }

    [Fact]
    public async Task Generate_WhenZeroDataRetentionEnabled_ShouldSendZdr()
    {
        var handler = new RecordingHttpMessageHandler
        {
            Responder = (_, _) => Task.FromResult(AiProviderTestFactory.Json(HttpStatusCode.OK, AiProviderTestFactory.OpenRouterSuccessBody()))
        };
        var sut = CreateSut(handler, zeroDataRetention: true);

        await sut.Generate(AiProviderTestFactory.OpenRouterRequest(), CancellationToken.None);

        using var payload = JsonDocument.Parse(handler.LastBody!);
        payload.RootElement.GetProperty("provider").GetProperty("zdr").GetBoolean().Should().BeTrue();
    }

    private static OpenRouterAiGenerationProvider CreateSut(
        RecordingHttpMessageHandler handler,
        IReadOnlyList<string>? models = null,
        CollectingLogger<OpenRouterAiGenerationProvider>? logger = null,
        TimeSpan? timeout = null,
        bool zeroDataRetention = false)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new OpenRouterOptions
        {
            BaseUrl = "https://openrouter.ai/api/v1",
            ApiKey = API_KEY,
            Models = models?.ToList() ?? ["fixture/openrouter-test"],
            Timeout = timeout ?? TimeSpan.FromSeconds(5),
            MaxInputChars = 12000,
            MaxOutputTokens = 1000,
            MaxRetryAfter = TimeSpan.FromHours(1),
            SiteUrl = "https://example.test/",
            AppName = "AssetBlock",
            ZeroDataRetention = zeroDataRetention
        });
        var factory = AiProviderTestFactory.CreateFactory(
            OpenRouterAiGenerationProvider.HTTP_CLIENT_NAME,
            handler,
            new Uri("https://openrouter.ai/api/v1/"));
        return new OpenRouterAiGenerationProvider(
            factory,
            options,
            logger ?? new CollectingLogger<OpenRouterAiGenerationProvider>());
    }
}
