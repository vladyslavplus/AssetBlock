using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AssetBlock.Infrastructure.Ai;

internal sealed class OllamaAiGenerationProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<OllamaOptions> optionsAccessor,
    ILogger<OllamaAiGenerationProvider> logger) : IAiGenerationProvider
{
    public const string HTTP_CLIENT_NAME = "Ollama";

    public AiProviderKind Kind => AiProviderKind.OLLAMA;
    public int MaxInputChars => optionsAccessor.Value.MaxInputChars;
    public int MaxOutputTokens => optionsAccessor.Value.MaxOutputTokens;
    public IReadOnlyList<string> OrderedModelIds =>
        string.IsNullOrWhiteSpace(optionsAccessor.Value.Model) ? [] : [optionsAccessor.Value.Model];

    public async Task<AiGenerationProviderResult> Generate(
        AiGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var options = optionsAccessor.Value;
        var modelId = options.Model;
        var expectedDigest = options.Digest;
        if (!AiConfigurationRules.IsModelId(modelId) || !AiConfigurationRules.IsSha256Digest(expectedDigest))
        {
            return Terminal(ErrorCodes.AI_MODEL_NOT_ALLOWED, started);
        }

        if (request.SystemPrompt.Length + request.UserPrompt.Length > options.MaxInputChars)
        {
            return Terminal(ErrorCodes.AI_INPUT_TOO_LARGE, started);
        }

        if (request.MaxOutputTokens > options.MaxOutputTokens)
        {
            return Terminal(ErrorCodes.AI_INVALID_REQUEST, started);
        }

        var client = httpClientFactory.CreateClient(HTTP_CLIENT_NAME);
        var budget = Stopwatch.StartNew();
        var digestCheck = await VerifyInstalledModel(
            client,
            options,
            modelId,
            expectedDigest,
            cancellationToken,
            started,
            budget);
        if (digestCheck is not null)
        {
            return digestCheck;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "api/chat");
        httpRequest.Content = new StringContent(BuildPayload(request, modelId), Encoding.UTF8, "application/json");

        using var timed = await AiTimedHttp.Send(
            client,
            httpRequest,
            AiTimeoutBudget.Remaining(options.Timeout, budget.Elapsed),
            OllamaOptions.MAX_RESPONSE_BYTES,
            cancellationToken);
        if (timed.TimedOut)
        {
            logger.LogWarning("Ollama generation timed out");
            return Retryable(ErrorCodes.AI_TIMEOUT, started);
        }

        if (timed.NetworkFailure)
        {
            logger.LogWarning("Ollama generation failed due to a network error");
            return Retryable(ErrorCodes.AI_PROVIDER_UNAVAILABLE, started);
        }

        var response = timed.Response!;
        logger.LogInformation("Ollama generation completed with HTTP {StatusCode}", (int)response.StatusCode);

        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            return Retryable(ErrorCodes.AI_PROVIDER_UNAVAILABLE, started);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Terminal(ErrorCodes.AI_INVALID_REQUEST, started);
        }

        if (timed.Oversized || timed.Body is null)
        {
            return Terminal(ErrorCodes.AI_INVALID_RESPONSE, started);
        }

        return ParseSuccess(timed.Body, started, modelId, expectedDigest);
    }

    private async Task<AiGenerationProviderResult?> VerifyInstalledModel(
        HttpClient client,
        OllamaOptions options,
        string modelId,
        string expectedDigest,
        CancellationToken cancellationToken,
        long started,
        Stopwatch budget)
    {
        using var tagsRequest = new HttpRequestMessage(HttpMethod.Get, "api/tags");
        using var timed = await AiTimedHttp.Send(
            client,
            tagsRequest,
            AiTimeoutBudget.Remaining(options.Timeout, budget.Elapsed),
            OllamaOptions.MAX_RESPONSE_BYTES,
            cancellationToken);
        if (timed.TimedOut)
        {
            logger.LogWarning("Ollama model tag lookup timed out");
            return Retryable(ErrorCodes.AI_TIMEOUT, started);
        }

        if (timed.NetworkFailure)
        {
            logger.LogWarning("Ollama model tag lookup failed due to a network error");
            return Retryable(ErrorCodes.AI_PROVIDER_UNAVAILABLE, started);
        }

        var response = timed.Response!;
        if ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)
        {
            return Retryable(ErrorCodes.AI_PROVIDER_UNAVAILABLE, started);
        }

        if (!response.IsSuccessStatusCode || timed.Oversized || timed.Body is null)
        {
            return Terminal(ErrorCodes.AI_INVALID_REQUEST, started);
        }

        try
        {
            using var document = JsonDocument.Parse(timed.Body);
            if (!document.RootElement.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            {
                return Terminal(ErrorCodes.AI_INVALID_RESPONSE, started);
            }

            foreach (var model in models.EnumerateArray())
            {
                var name = model.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var digest = model.TryGetProperty("digest", out var digestEl) ? digestEl.GetString() : null;
                if (string.Equals(name, modelId, StringComparison.Ordinal)
                    && string.Equals(digest, expectedDigest, StringComparison.Ordinal))
                {
                    return null;
                }
            }

            return Terminal(ErrorCodes.AI_MODEL_NOT_ALLOWED, started);
        }
        catch (JsonException)
        {
            return Terminal(ErrorCodes.AI_INVALID_RESPONSE, started);
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
            var root = document.RootElement;
            var actualModel = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
            int? inputTokens = ReadInt(root, "prompt_eval_count");
            int? outputTokens = ReadInt(root, "eval_count");

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
                    ErrorCodes.AI_MODEL_NOT_ALLOWED,
                    null);
            }

            var modelRevision = expectedDigest;

            if (!root.TryGetProperty("message", out var message)
                || !message.TryGetProperty("content", out var content))
            {
                return Terminal(ErrorCodes.AI_INVALID_RESPONSE, started, actualModel, inputTokens, outputTokens, modelRevision);
            }

            string? structuredJson = content.ValueKind switch
            {
                JsonValueKind.String => content.GetString(),
                JsonValueKind.Object => content.GetRawText(),
                _ => null
            };

            if (string.IsNullOrWhiteSpace(structuredJson))
            {
                return Terminal(ErrorCodes.AI_INVALID_RESPONSE, started, actualModel, inputTokens, outputTokens, modelRevision);
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
            return Terminal(ErrorCodes.AI_INVALID_RESPONSE, started);
        }
    }

    private static int? ReadInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var el) && el.TryGetInt32(out var value) ? value : null;

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
