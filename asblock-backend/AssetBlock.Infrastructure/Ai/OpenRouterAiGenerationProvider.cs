using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AssetBlock.Infrastructure.Ai;

internal sealed class OpenRouterAiGenerationProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<OpenRouterOptions> optionsAccessor,
    ILogger<OpenRouterAiGenerationProvider> logger) : IAiGenerationProvider
{
    public const string HTTP_CLIENT_NAME = "OpenRouter";

    public AiProviderKind Kind => AiProviderKind.OPENROUTER;
    public int MaxInputChars => optionsAccessor.Value.MaxInputChars;
    public int MaxOutputTokens => optionsAccessor.Value.MaxOutputTokens;
    public IReadOnlyList<string> OrderedModelIds => optionsAccessor.Value.Models;

    public async Task<AiGenerationProviderResult> Generate(
        AiGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var options = optionsAccessor.Value;
        var models = options.Models;
        if (models.Count == 0 || models.Any(model => !AiConfigurationRules.IsModelId(model)))
        {
            return Terminal(ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED, started);
        }

        var promptChars = request.SystemPrompt.Length + request.UserPrompt.Length;
        if (promptChars > options.MaxInputChars)
        {
            return Terminal(ErrorCodes.ERR_AI_INPUT_TOO_LARGE, started);
        }

        if (request.MaxOutputTokens > options.MaxOutputTokens)
        {
            return Terminal(ErrorCodes.ERR_AI_INVALID_REQUEST, started);
        }

        var client = httpClientFactory.CreateClient(HTTP_CLIENT_NAME);
        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        httpRequest.Content = new StringContent(BuildPayload(request, options), Encoding.UTF8, "application/json");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        httpRequest.Headers.TryAddWithoutValidation("X-OpenRouter-Metadata", "enabled");
        if (!string.IsNullOrWhiteSpace(options.SiteUrl))
        {
            httpRequest.Headers.TryAddWithoutValidation("HTTP-Referer", options.SiteUrl);
        }

        if (!string.IsNullOrWhiteSpace(options.AppName))
        {
            httpRequest.Headers.TryAddWithoutValidation("X-OpenRouter-Title", options.AppName);
        }

        using var timed = await AiTimedHttp.Send(
            client,
            httpRequest,
            options.Timeout,
            OpenRouterOptions.MAX_RESPONSE_BYTES,
            cancellationToken);
        if (timed.TimedOut)
        {
            logger.LogWarning("OpenRouter generation timed out");
            return Retryable(ErrorCodes.ERR_AI_TIMEOUT, started, retryAfter: null);
        }

        if (timed.NetworkFailure)
        {
            logger.LogWarning("OpenRouter generation failed due to a network error");
            return Retryable(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, started, retryAfter: null);
        }

        var response = timed.Response!;
        var retryAfter = RetryAfterParser.Parse(response.Headers, options.MaxRetryAfter);
        logger.LogInformation("OpenRouter generation completed with HTTP {StatusCode}", (int)response.StatusCode);

        if (IsRetryableStatus(response.StatusCode))
        {
            var code = response.StatusCode == HttpStatusCode.TooManyRequests
                ? ErrorCodes.ERR_AI_RATE_LIMITED
                : response.StatusCode == HttpStatusCode.RequestTimeout
                    ? ErrorCodes.ERR_AI_TIMEOUT
                    : ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE;
            return Retryable(code, started, retryAfter);
        }

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return Terminal(ErrorCodes.ERR_AI_UNAUTHORIZED, started);
        }

        if (response.StatusCode == HttpStatusCode.PaymentRequired)
        {
            return Terminal(ErrorCodes.ERR_AI_PAYMENT_REQUIRED, started);
        }

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            return Terminal(ErrorCodes.ERR_AI_FORBIDDEN, started);
        }

        if ((int)response.StatusCode is >= 400 and < 500)
        {
            return Terminal(ErrorCodes.ERR_AI_INVALID_REQUEST, started);
        }

        if (!response.IsSuccessStatusCode)
        {
            return Retryable(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, started, retryAfter);
        }

        if (timed.Oversized || timed.Body is null)
        {
            return Terminal(ErrorCodes.ERR_AI_INVALID_RESPONSE, started);
        }

        return ParseSuccess(timed.Body, started);
    }

    private AiGenerationProviderResult ParseSuccess(string body, long started)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var actualModel = root.TryGetProperty("model", out var modelEl) ? modelEl.GetString() : null;
            var requestId = root.TryGetProperty("id", out var idEl) ? Truncate(idEl.GetString()) : null;
            var upstream = ReadUpstreamProvider(root);

            int? inputTokens = null;
            int? outputTokens = null;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out var prompt) && prompt.TryGetInt32(out var promptTokens))
                {
                    inputTokens = promptTokens;
                }

                if (usage.TryGetProperty("completion_tokens", out var completion) && completion.TryGetInt32(out var completionTokens))
                {
                    outputTokens = completionTokens;
                }
            }

            if (!IsConfiguredModel(actualModel, optionsAccessor.Value.Models))
            {
                return new AiGenerationProviderResult(
                    AiGenerationOutcomeKind.TERMINAL_FAILURE,
                    false,
                    AiProviderKind.OPENROUTER,
                    actualModel,
                    upstream,
                    inputTokens,
                    outputTokens,
                    Stopwatch.GetElapsedTime(started),
                    requestId,
                    null,
                    ErrorCodes.ERR_AI_MODEL_NOT_ALLOWED,
                    null);
            }

            if (!TryReadContent(root, out var structuredJson))
            {
                return Terminal(ErrorCodes.ERR_AI_INVALID_RESPONSE, started, actualModel, upstream, inputTokens, outputTokens, requestId);
            }

            return new AiGenerationProviderResult(
                AiGenerationOutcomeKind.SUCCESS,
                false,
                AiProviderKind.OPENROUTER,
                actualModel,
                upstream,
                inputTokens,
                outputTokens,
                Stopwatch.GetElapsedTime(started),
                requestId,
                null,
                null,
                structuredJson);
        }
        catch (JsonException)
        {
            return Terminal(ErrorCodes.ERR_AI_INVALID_RESPONSE, started);
        }
    }

    private static bool IsConfiguredModel(string? actualModel, IReadOnlyList<string> configuredModels) =>
        !string.IsNullOrWhiteSpace(actualModel)
        && configuredModels.Contains(actualModel, StringComparer.Ordinal);

    private static string? ReadUpstreamProvider(JsonElement root)
    {
        if (!root.TryGetProperty("openrouter_metadata", out var metadata)
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("endpoints", out var endpoints)
            || endpoints.ValueKind != JsonValueKind.Object
            || !endpoints.TryGetProperty("available", out var available)
            || available.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var candidate in available.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object
                || !candidate.TryGetProperty("selected", out var selected)
                || selected.ValueKind != JsonValueKind.True)
            {
                continue;
            }

            return ReadEndpointName(candidate);
        }

        return null;
    }

    private static string? ReadEndpointName(JsonElement endpoint)
    {
        if (endpoint.TryGetProperty("provider", out var providerEl))
        {
            if (providerEl.ValueKind == JsonValueKind.String)
            {
                return Truncate(providerEl.GetString());
            }

            if (providerEl.ValueKind == JsonValueKind.Object
                && providerEl.TryGetProperty("name", out var nestedName))
            {
                return Truncate(nestedName.GetString());
            }
        }

        return endpoint.TryGetProperty("name", out var nameEl)
            ? Truncate(nameEl.GetString())
            : null;
    }

    private static bool TryReadContent(JsonElement root, out string structuredJson)
    {
        structuredJson = string.Empty;
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return false;
        }

        var first = choices[0];
        if (!first.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content))
        {
            return false;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            structuredJson = content.GetString() ?? string.Empty;
            return !string.IsNullOrWhiteSpace(structuredJson);
        }

        if (content.ValueKind == JsonValueKind.Object)
        {
            structuredJson = content.GetRawText();
            return true;
        }

        return false;
    }

    private static string BuildPayload(AiGenerationRequest request, OpenRouterOptions options)
    {
        var models = new JsonArray();
        foreach (var model in options.Models)
        {
            models.Add(model);
        }

        var provider = new JsonObject
        {
            ["require_parameters"] = true,
            ["data_collection"] = "deny"
        };
        if (options.ZeroDataRetention)
        {
            provider["zdr"] = true;
        }

        var payload = new JsonObject
        {
            ["models"] = models,
            ["max_tokens"] = request.MaxOutputTokens,
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = request.SystemPrompt },
                new JsonObject { ["role"] = "user", ["content"] = request.UserPrompt }),
            ["response_format"] = new JsonObject
            {
                ["type"] = "json_schema",
                ["json_schema"] = new JsonObject
                {
                    ["name"] = "listing_suggestion",
                    ["strict"] = true,
                    ["schema"] = JsonNode.Parse(request.ResponseSchemaJson)
                }
            },
            ["provider"] = provider
        };

        return payload.ToJsonString();
    }

    private static bool IsRetryableStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
        || (int)statusCode >= 500;

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length <= ListingSuggestionBounds.REQUEST_ID_MAX_LENGTH
            ? value
            : value[..ListingSuggestionBounds.REQUEST_ID_MAX_LENGTH];
    }

    private static AiGenerationProviderResult Terminal(
        string errorCode,
        long started,
        string? actualModel = null,
        string? upstream = null,
        int? inputTokens = null,
        int? outputTokens = null,
        string? requestId = null) =>
        new(
            AiGenerationOutcomeKind.TERMINAL_FAILURE,
            false,
            AiProviderKind.OPENROUTER,
            actualModel,
            upstream,
            inputTokens,
            outputTokens,
            Stopwatch.GetElapsedTime(started),
            requestId,
            null,
            errorCode,
            null);

    private static AiGenerationProviderResult Retryable(string errorCode, long started, TimeSpan? retryAfter) =>
        new(
            AiGenerationOutcomeKind.RETRYABLE_FAILURE,
            true,
            AiProviderKind.OPENROUTER,
            null,
            null,
            null,
            null,
            Stopwatch.GetElapsedTime(started),
            null,
            retryAfter,
            errorCode,
            null);
}
