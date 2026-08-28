using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AssetBlock.Application.Ai;

internal sealed class ListingSuggestionOrchestrator(
    IOptions<AiOptions> aiOptions,
    IAiGenerationProviderRegistry providers,
    IAiTelemetry telemetry,
    ILogger<ListingSuggestionOrchestrator> logger) : IListingSuggestionOrchestrator
{
    private static readonly JsonSerializerOptions _suggestionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public async Task<ListingSuggestionResult> Generate(
        ListingSuggestionGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var options = aiOptions.Value;
        var parsedProvider = AiProviderParser.TryParse(options.Provider, out var requestedProvider);
        using var activity = telemetry.StartActivity();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!options.Enabled)
            {
                var disabledProvider = parsedProvider ? requestedProvider : AiProviderKind.OPENROUTER;
                return Complete(
                    DisabledResult(disabledProvider),
                    started,
                    allowlistedModel: null,
                    requestId: null);
            }

            if (!parsedProvider)
            {
                return Complete(
                    Terminal(AiProviderKind.OPENROUTER, ErrorCodes.ERR_AI_ERROR, TimeSpan.Zero),
                    started,
                    allowlistedModel: null,
                    requestId: null);
            }

            if (!string.Equals(options.PromptPolicyVersion, ListingCopilotPrompt.POLICY_VERSION, StringComparison.Ordinal))
            {
                return Complete(
                    Terminal(requestedProvider, ErrorCodes.ERR_AI_INVALID_REQUEST, TimeSpan.Zero),
                    started,
                    allowlistedModel: null,
                    requestId: null);
            }

            if (!providers.TryGet(requestedProvider, out var provider))
            {
                return Complete(
                    Terminal(requestedProvider, ErrorCodes.ERR_AI_ERROR, TimeSpan.Zero),
                    started,
                    allowlistedModel: null,
                    requestId: null);
            }

            var inputError = ValidateInput(request, provider.MaxInputChars);
            if (inputError is not null)
            {
                return Complete(
                    Terminal(requestedProvider, inputError, TimeSpan.Zero),
                    started,
                    allowlistedModel: null,
                    requestId: null);
            }

            var generationRequest = new AiGenerationRequest(
                requestedProvider,
                ListingCopilotPrompt.POLICY_VERSION,
                ListingCopilotPrompt.BuildSystemPrompt(),
                ListingCopilotPrompt.BuildUserPrompt(request),
                ListingSuggestionJsonSchema.ForAllowlists(request.AllowedCategories, request.AllowedTags),
                provider.MaxOutputTokens);

            var providerResult = await provider.Generate(generationRequest, cancellationToken);
            if (providerResult.Outcome != AiGenerationOutcomeKind.SUCCESS
                || string.IsNullOrWhiteSpace(providerResult.StructuredJson))
            {
                logger.LogInformation(
                    "Listing suggestion generation finished with outcome {Outcome} and error {ErrorCode}",
                    providerResult.Outcome,
                    providerResult.ErrorCode);
                return Complete(
                    MapProviderFailure(providerResult),
                    started,
                    allowlistedModel: AllowlistedModel(provider, providerResult.ActualModel),
                    requestId: providerResult.RequestId);
            }

            if (!TryParseSuggestion(providerResult.StructuredJson, out var draft, out var parseError))
            {
                logger.LogInformation("Listing suggestion JSON failed schema validation");
                return Complete(
                    Terminal(
                        requestedProvider,
                        parseError,
                        providerResult.Latency,
                        providerResult.ActualModel,
                        providerResult.UpstreamProvider,
                        providerResult.InputTokens,
                        providerResult.OutputTokens,
                        providerResult.RequestId,
                        providerResult.ModelRevision),
                    started,
                    allowlistedModel: AllowlistedModel(provider, providerResult.ActualModel),
                    requestId: providerResult.RequestId);
            }

            if (!TryResolveAllowlists(draft, request, out var suggestion, out var allowlistError))
            {
                logger.LogInformation("Listing suggestion failed allowlist resolution with {ErrorCode}", allowlistError);
                return Complete(
                    Terminal(
                        requestedProvider,
                        allowlistError,
                        providerResult.Latency,
                        providerResult.ActualModel,
                        providerResult.UpstreamProvider,
                        providerResult.InputTokens,
                        providerResult.OutputTokens,
                        providerResult.RequestId,
                        providerResult.ModelRevision),
                    started,
                    allowlistedModel: AllowlistedModel(provider, providerResult.ActualModel),
                    requestId: providerResult.RequestId);
            }

            var success = new ListingSuggestionResult(
                AiGenerationOutcomeKind.SUCCESS,
                false,
                suggestion,
                requestedProvider,
                providerResult.ActualModel,
                providerResult.UpstreamProvider,
                providerResult.InputTokens,
                providerResult.OutputTokens,
                providerResult.Latency,
                providerResult.RequestId,
                null,
                null,
                providerResult.ModelRevision);
            return Complete(
                success,
                started,
                AllowlistedModel(provider, providerResult.ActualModel),
                providerResult.RequestId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            telemetry.Record(
                parsedProvider ? requestedProvider : null,
                null,
                AiTelemetryOutcome.CANCELLED,
                Stopwatch.GetElapsedTime(started),
                null,
                null,
                null);
            throw;
        }
    }

    private static string? AllowlistedModel(IAiGenerationProvider provider, string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        return provider.OrderedModelIds.Contains(modelId, StringComparer.Ordinal) ? modelId : null;
    }

    private ListingSuggestionResult Complete(
        ListingSuggestionResult result,
        long started,
        string? allowlistedModel,
        string? requestId)
    {
        var duration = result.Latency > TimeSpan.Zero
            ? result.Latency
            : Stopwatch.GetElapsedTime(started);
        telemetry.Record(
            result.RequestedProvider,
            allowlistedModel,
            ToTelemetryOutcome(result.Outcome),
            duration,
            result.InputTokens,
            result.OutputTokens,
            requestId);
        return result with { Latency = duration };
    }

    private static string? ValidateInput(ListingSuggestionGenerationRequest request, int maxInputChars)
    {
        if (!string.Equals(request.PromptPolicyVersion, ListingCopilotPrompt.POLICY_VERSION, StringComparison.Ordinal))
        {
            return ErrorCodes.ERR_AI_INVALID_REQUEST;
        }

        if (request.AllowedCategories.Count is 0 or > ListingSuggestionBounds.MAX_ALLOWLIST_CATEGORIES)
        {
            return ErrorCodes.ERR_AI_INVALID_REQUEST;
        }

        if (request.AllowedTags.Count > ListingSuggestionBounds.MAX_ALLOWLIST_TAGS)
        {
            return ErrorCodes.ERR_AI_INVALID_REQUEST;
        }

        foreach (var category in request.AllowedCategories)
        {
            if (string.IsNullOrWhiteSpace(category) || category.Length > ListingSuggestionBounds.CATEGORY_NAME_MAX_LENGTH)
            {
                return ErrorCodes.ERR_AI_INVALID_REQUEST;
            }
        }

        foreach (var tag in request.AllowedTags)
        {
            if (string.IsNullOrWhiteSpace(tag) || tag.Length > ListingSuggestionBounds.TAG_NAME_MAX_LENGTH)
            {
                return ErrorCodes.ERR_AI_INVALID_REQUEST;
            }
        }

        if (request.Archive.SampleEntryPaths.Count > ListingSuggestionBounds.MAX_ARCHIVE_SAMPLE_PATHS
            || request.Archive.Manifests.Count > ListingSuggestionBounds.MAX_MANIFESTS)
        {
            return ErrorCodes.ERR_AI_INVALID_REQUEST;
        }

        if (request.Readme is not null
            && (request.Readme.FileName.Length > ListingSuggestionBounds.README_FILE_NAME_MAX_LENGTH
                || request.Readme.Text.Length > ListingSuggestionBounds.README_TEXT_MAX_CHARS))
        {
            return ErrorCodes.ERR_AI_INPUT_TOO_LARGE;
        }

        var promptLength = ListingCopilotPrompt.BuildSystemPrompt().Length
            + ListingCopilotPrompt.BuildUserPrompt(request).Length;
        return promptLength > maxInputChars ? ErrorCodes.ERR_AI_INPUT_TOO_LARGE : null;
    }

    private static bool TryParseSuggestion(
        string json,
        out ListingSuggestionDraft draft,
        out string errorCode)
    {
        draft = null!;
        errorCode = ErrorCodes.ERR_AI_INVALID_RESPONSE;
        try
        {
            var parsed = JsonSerializer.Deserialize<ListingSuggestionDraft>(json, _suggestionJsonOptions);
            if (parsed is null
                || string.IsNullOrWhiteSpace(parsed.Title)
                || parsed.Title.Length > ListingSuggestionBounds.TITLE_MAX_LENGTH
                || parsed.Description.Length > ListingSuggestionBounds.DESCRIPTION_MAX_LENGTH
                || string.IsNullOrWhiteSpace(parsed.Category)
                || parsed.Tags.Count > ListingSuggestionBounds.MAX_SUGGESTED_TAGS)
            {
                return false;
            }

            foreach (var tag in parsed.Tags)
            {
                if (string.IsNullOrWhiteSpace(tag) || tag.Length > ListingSuggestionBounds.TAG_NAME_MAX_LENGTH)
                {
                    return false;
                }
            }

            draft = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool TryResolveAllowlists(
        ListingSuggestionDraft draft,
        ListingSuggestionGenerationRequest request,
        out ListingSuggestion suggestion,
        out string errorCode)
    {
        suggestion = null!;
        var category = request.AllowedCategories.FirstOrDefault(c =>
            string.Equals(c, draft.Category, StringComparison.Ordinal));
        if (category is null)
        {
            errorCode = ErrorCodes.ERR_AI_CATEGORY_NOT_ALLOWED;
            return false;
        }

        var tags = new List<string>();
        foreach (var tag in draft.Tags)
        {
            var allowed = request.AllowedTags.FirstOrDefault(t =>
                string.Equals(t, tag, StringComparison.Ordinal));
            if (allowed is null)
            {
                errorCode = ErrorCodes.ERR_AI_TAGS_NOT_ALLOWED;
                return false;
            }

            if (!tags.Contains(allowed, StringComparer.Ordinal))
            {
                tags.Add(allowed);
            }
        }

        suggestion = new ListingSuggestion(draft.Title.Trim(), draft.Description, category, tags);
        errorCode = string.Empty;
        return true;
    }

    private static ListingSuggestionResult DisabledResult(AiProviderKind provider) =>
        new(
            AiGenerationOutcomeKind.DISABLED,
            false,
            null,
            provider,
            null,
            null,
            null,
            null,
            TimeSpan.Zero,
            null,
            null,
            ErrorCodes.ERR_AI_DISABLED);

    private static ListingSuggestionResult Terminal(
        AiProviderKind provider,
        string errorCode,
        TimeSpan latency,
        string? actualModel = null,
        string? upstreamProvider = null,
        int? inputTokens = null,
        int? outputTokens = null,
        string? requestId = null,
        string? modelRevision = null) =>
        new(
            AiGenerationOutcomeKind.TERMINAL_FAILURE,
            false,
            null,
            provider,
            actualModel,
            upstreamProvider,
            inputTokens,
            outputTokens,
            latency,
            requestId,
            null,
            errorCode,
            modelRevision);

    private static ListingSuggestionResult MapProviderFailure(AiGenerationProviderResult result) =>
        new(
            result.Outcome == AiGenerationOutcomeKind.RETRYABLE_FAILURE
                ? AiGenerationOutcomeKind.RETRYABLE_FAILURE
                : AiGenerationOutcomeKind.TERMINAL_FAILURE,
            result.IsRetryable,
            null,
            result.RequestedProvider,
            result.ActualModel,
            result.UpstreamProvider,
            result.InputTokens,
            result.OutputTokens,
            result.Latency,
            result.RequestId,
            result.RetryAfter,
            result.ErrorCode ?? ErrorCodes.ERR_AI_ERROR,
            result.ModelRevision);

    private static AiTelemetryOutcome ToTelemetryOutcome(AiGenerationOutcomeKind outcome) => outcome switch
    {
        AiGenerationOutcomeKind.SUCCESS => AiTelemetryOutcome.SUCCESS,
        AiGenerationOutcomeKind.DISABLED => AiTelemetryOutcome.DISABLED,
        AiGenerationOutcomeKind.RETRYABLE_FAILURE => AiTelemetryOutcome.RETRYABLE,
        _ => AiTelemetryOutcome.TERMINAL
    };

    private sealed record ListingSuggestionDraft(
        string Title,
        string Description,
        string Category,
        IReadOnlyList<string> Tags);
}
