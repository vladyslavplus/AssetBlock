using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Ai;

internal sealed class OllamaAiGenerationProvider : IAiGenerationProvider
{
    public const string HTTP_CLIENT_NAME = "Ollama";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptions<OllamaOptions> _optionsAccessor;
    private readonly ILogger<OllamaAiGenerationProvider> _logger;
    private readonly TimedHttpSender _timedSender;

    public OllamaAiGenerationProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaOptions> optionsAccessor,
        ILogger<OllamaAiGenerationProvider> logger)
        : this(httpClientFactory, optionsAccessor, logger, AiTimedHttp.Send)
    {
    }

    internal OllamaAiGenerationProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<OllamaOptions> optionsAccessor,
        ILogger<OllamaAiGenerationProvider> logger,
        TimedHttpSender timedSender)
    {
        _httpClientFactory = httpClientFactory;
        _optionsAccessor = optionsAccessor;
        _logger = logger;
        _timedSender = timedSender;
    }

    public AiProviderKind Kind => AiProviderKind.OLLAMA;
    public int MaxInputChars => _optionsAccessor.Value.MaxInputChars;
    public int MaxOutputTokens => _optionsAccessor.Value.MaxOutputTokens;
    public IReadOnlyList<string> OrderedModelIds =>
        string.IsNullOrWhiteSpace(_optionsAccessor.Value.Model) ? [] : [_optionsAccessor.Value.Model];

    public async Task<AiGenerationProviderResult> Generate(
        AiGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        OllamaOptions options = _optionsAccessor.Value;
        var modelId = options.Model;
        var expectedDigest = options.Digest;
        if (!AiConfigurationRules.IsModelId(modelId) || !AiConfigurationRules.IsSha256Digest(expectedDigest))
        {
            return Terminal(ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED, started);
        }

        if (request.SystemPrompt.Length + request.UserPrompt.Length > options.MaxInputChars)
        {
            return Terminal(ErrorCodes.ERR_AI_INPUT_TOO_LARGE, started);
        }

        if (request.MaxOutputTokens > options.MaxOutputTokens)
        {
            return Terminal(ErrorCodes.ERR_AI_INVALID_REQUEST, started);
        }

        HttpClient client = _httpClientFactory.CreateClient(HTTP_CLIENT_NAME);
        var budget = Stopwatch.StartNew();
        AiGenerationProviderResult? digestCheck = await VerifyInstalledModel(
            client,
            options,
            modelId,
            expectedDigest,
            started,
            budget,
            cancellationToken);
        if (digestCheck is not null)
        {
            return digestCheck;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/chat");
        httpRequest.Content = new StringContent(BuildPayload(request, modelId), Encoding.UTF8, "application/json");

        using AiTimedHttpResult timed = await _timedSender(
            client,
            httpRequest,
            AiTimeoutBudget.Remaining(options.Timeout, budget.Elapsed),
            OllamaOptions.MAX_RESPONSE_BYTES,
            cancellationToken);
        if (timed.TimedOut)
        {
            _logger.LogWarning("Ollama generation timed out");
            return Retryable(ErrorCodes.ERR_AI_TIMEOUT, started);
        }

        if (timed.NetworkFailure)
        {
            _logger.LogWarning("Ollama generation failed due to a network error");
            return Retryable(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, started);
        }

        HttpResponseMessage response = timed.Response!;
        _logger.LogInformation("Ollama generation completed with HTTP {StatusCode}", (int)response.StatusCode);

        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            return Retryable(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, started);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Terminal(ErrorCodes.ERR_AI_INVALID_REQUEST, started);
        }

        if (timed.Oversized || timed.Body is null)
        {
            return Terminal(ErrorCodes.ERR_AI_INVALID_RESPONSE, started);
        }

        return ParseSuccess(timed.Body, started, modelId, expectedDigest);
    }

    private async Task<AiGenerationProviderResult?> VerifyInstalledModel(
        HttpClient client,
        OllamaOptions options,
        string modelId,
        string expectedDigest,
        long started,
        Stopwatch budget,
        CancellationToken cancellationToken)
    {
        using var tagsRequest = new HttpRequestMessage(HttpMethod.Get, "api/tags");
        using AiTimedHttpResult timed = await _timedSender(
            client,
            tagsRequest,
            AiTimeoutBudget.Remaining(options.Timeout, budget.Elapsed),
            OllamaOptions.MAX_RESPONSE_BYTES,
            cancellationToken);
        if (timed.TimedOut)
        {
            _logger.LogWarning("Ollama model tag lookup timed out");
            return Retryable(ErrorCodes.ERR_AI_TIMEOUT, started);
        }

        if (timed.NetworkFailure)
        {
            _logger.LogWarning("Ollama model tag lookup failed due to a network error");
            return Retryable(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, started);
        }

        HttpResponseMessage response = timed.Response!;
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            return Retryable(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, started);
        }

        if (!response.IsSuccessStatusCode || timed.Oversized || timed.Body is null)
        {
            return Terminal(ErrorCodes.ERR_AI_INVALID_REQUEST, started);
        }

        try
        {
            using var document = JsonDocument.Parse(timed.Body);
            if (!document.RootElement.TryGetProperty("models", out JsonElement models) || models.ValueKind != JsonValueKind.Array)
            {
                return Terminal(ErrorCodes.ERR_AI_INVALID_RESPONSE, started);
            }

            foreach (JsonElement model in models.EnumerateArray())
            {
                var name = model.TryGetProperty("name", out JsonElement nameEl) ? nameEl.GetString() : null;
                var digest = model.TryGetProperty("digest", out JsonElement digestEl) ? digestEl.GetString() : null;
                if (string.Equals(name, modelId, StringComparison.Ordinal)
                    && string.Equals(digest, expectedDigest, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            return Terminal(ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED, started);
        }
        catch (JsonException)
        {
            return Terminal(ErrorCodes.ERR_AI_INVALID_RESPONSE, started);
        }
    }

    private static AiGenerationProviderResult ParseSuccess(
        string body,
        long started,
        string modelId,
        string expectedDigest)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            var actualModel = root.TryGetProperty("model", out JsonElement modelEl) ? modelEl.GetString() : null;
            var inputTokens = ReadInt(root, "prompt_eval_count");
            var outputTokens = ReadInt(root, "eval_count");

            if (string.IsNullOrWhiteSpace(actualModel)
                || !string.Equals(actualModel, modelId, StringComparison.Ordinal))
            {
                return new AiGenerationProviderResult(
                    AiGenerationOutcomeKind.TERMINAL_FAILURE,
                    false,
                    AiProviderKind.OLLAMA,
                    actualModel,
                    null,
                    inputTokens,
                    outputTokens,
                    Stopwatch.GetElapsedTime(started),
                    null,
                    null,
                    ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED,
                    null);
            }

            var modelRevision = expectedDigest;

            if (!root.TryGetProperty("message", out JsonElement message)
                || !message.TryGetProperty("content", out JsonElement content))
            {
                return Terminal(ErrorCodes.ERR_AI_INVALID_RESPONSE, started, actualModel, inputTokens, outputTokens, modelRevision);
            }

            var structuredJson = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Object => content.GetRawText(),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(structuredJson))
            {
                return Terminal(ErrorCodes.ERR_AI_INVALID_RESPONSE, started, actualModel, inputTokens, outputTokens, modelRevision);
            }

            return new AiGenerationProviderResult(
                AiGenerationOutcomeKind.SUCCESS,
                false,
                AiProviderKind.OLLAMA,
                actualModel,
                null,
                inputTokens,
                outputTokens,
                Stopwatch.GetElapsedTime(started),
                null,
                null,
                null,
                structuredJson,
                modelRevision);
        }
        catch (JsonException)
        {
            return Terminal(ErrorCodes.ERR_AI_INVALID_RESPONSE, started);
        }
    }

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement el) && el.TryGetInt32(out var value) ? value : null;

    private static string BuildPayload(AiGenerationRequest request, string modelId)
    {
        var payload = new JsonObject
        {
            ["model"] = modelId,
            ["stream"] = false,
            ["format"] = JsonNode.Parse(request.ResponseSchemaJson),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }),
            ["options"] = new JsonObject { ["num_predict"] = request.MaxOutputTokens }
        };
        return payload.ToJsonString();
    }

    private static AiGenerationProviderResult Terminal(
        string errorCode,
        long started,
        string? actualModel = null,
        int? inputTokens = null,
        int? outputTokens = null,
        string? modelRevision = null) =>
        new(
            AiGenerationOutcomeKind.TERMINAL_FAILURE,
            false,
            AiProviderKind.OLLAMA,
            actualModel,
            null,
            inputTokens,
            outputTokens,
            Stopwatch.GetElapsedTime(started),
            null,
            null,
            errorCode,
            null,
            modelRevision);

    private static AiGenerationProviderResult Retryable(string errorCode, long started) =>
        new(
            AiGenerationOutcomeKind.RETRYABLE_FAILURE,
            true,
            AiProviderKind.OLLAMA,
            null,
            null,
            null,
            null,
            Stopwatch.GetElapsedTime(started),
            null,
            null,
            errorCode,
            null);
}
