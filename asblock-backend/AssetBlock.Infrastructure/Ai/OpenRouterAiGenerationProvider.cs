using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
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
        OpenRouterOptions options = optionsAccessor.Value;
        List<string> models = options.Models;
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

        HttpClient client = httpClientFactory.CreateClient(HTTP_CLIENT_NAME);
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

        using AiTimedHttpResult timed = await AiTimedHttp.Send(
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

        HttpResponseMessage response = timed.Response!;
        TimeSpan? retryAfter = RetryAfterParser.Parse(response.Headers, options.MaxRetryAfter);
        logger.LogInformation("OpenRouter generation completed with HTTP {StatusCode}", (int)response.StatusCode);

        if (AiHttpStatusClassifier.IsRetryable(response.StatusCode))
        {
            var code = response.StatusCode switch
            {
                HttpStatusCode.TooManyRequests => ErrorCodes.ERR_AI_RATE_LIMITED,
                HttpStatusCode.RequestTimeout => ErrorCodes.ERR_AI_TIMEOUT,
                _ => ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE
            };
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
            return Terminal(ErrorCodes.ERR_AI_PROVIDER_UNAVAILABLE, started);
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
            JsonElement root = document.RootElement;
            var actualModel = root.TryGetProperty("model", out JsonElement modelEl) ? modelEl.GetString() : null;
            var requestId = root.TryGetProperty("id", out JsonElement idEl) ? Truncate(idEl.GetString()) : null;
            var upstream = ReadUpstreamProvider(root);

            int? inputTokens = null;
            int? outputTokens = null;
            if (root.TryGetProperty("usage", out JsonElement usage) && usage.ValueKind == JsonValueKind.Object)
            {
                if (usage.TryGetProperty("prompt_tokens", out JsonElement prompt) && prompt.TryGetInt32(out var promptTokens))
                {
                    inputTokens = promptTokens;
                }

                if (usage.TryGetProperty("completion_tokens", out JsonElement completion) && completion.TryGetInt32(out var completionTokens))
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
        if (!root.TryGetProperty("openrouter_metadata", out JsonElement metadata)
            || metadata.ValueKind != JsonValueKind.Object
            || !metadata.TryGetProperty("endpoints", out JsonElement endpoints)
            || endpoints.ValueKind != JsonValueKind.Object
            || !endpoints.TryGetProperty("available", out JsonElement available)
            || available.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (JsonElement candidate in available.EnumerateArray())
        {
            if (candidate.ValueKind != JsonValueKind.Object
                || !candidate.TryGetProperty("selected", out JsonElement selected)
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
        if (endpoint.TryGetProperty("provider", out JsonElement providerEl))
        {
            if (providerEl.ValueKind == JsonValueKind.String)
            {
                return Truncate(providerEl.GetString());
            }

            if (providerEl.ValueKind == JsonValueKind.Object
                && providerEl.TryGetProperty("name", out JsonElement nestedName))
            {
                return Truncate(nestedName.GetString());
            }
        }

        return endpoint.TryGetProperty("name", out JsonElement nameEl)
            ? Truncate(nameEl.GetString())
            : null;
    }

    private static bool TryReadContent(JsonElement root, out string structuredJson)
    {
        structuredJson = string.Empty;
        if (!root.TryGetProperty("choices", out JsonElement choices) || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
        {
            return false;
        }

        JsonElement first = choices[0];
        if (!first.TryGetProperty("message", out JsonElement message) || !message.TryGetProperty("content", out JsonElement content))
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
