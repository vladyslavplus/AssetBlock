using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto;

public sealed record SafeReadmeExcerpt(string FileName, string Text);

public sealed record NormalizedArchiveMetadata(
    string Format,
    int EntryCount,
    long TotalExpandedBytes,
    IReadOnlyList<string> SampleEntryPaths,
    IReadOnlyList<RecognizedManifestItem> Manifests);

public sealed record ListingSuggestionGenerationRequest(
    string PromptPolicyVersion,
    SafeReadmeExcerpt? Readme,
    NormalizedArchiveMetadata Archive,
    IReadOnlyList<string> AllowedCategories,
    IReadOnlyList<string> AllowedTags);

public sealed record ListingSuggestion(
    string Title,
    string Description,
    string Category,
    IReadOnlyList<string> Tags);

public sealed record ListingSuggestionResult(
    AiGenerationOutcomeKind Outcome,
    bool IsRetryable,
    ListingSuggestion? Suggestion,
    AiProviderKind RequestedProvider,
    string? ActualModel,
    string? UpstreamProvider,
    int? InputTokens,
    int? OutputTokens,
    TimeSpan Latency,
    string? RequestId,
    TimeSpan? RetryAfter,
    string? ErrorCode,
    string? ModelRevision = null);

public sealed record AiGenerationRequest(
    AiProviderKind RequestedProvider,
    string PromptPolicyVersion,
    string SystemPrompt,
    string UserPrompt,
    string ResponseSchemaJson,
    int MaxOutputTokens);

public sealed record AiGenerationProviderResult(
    AiGenerationOutcomeKind Outcome,
    bool IsRetryable,
    AiProviderKind RequestedProvider,
    string? ActualModel,
    string? UpstreamProvider,
    int? InputTokens,
    int? OutputTokens,
    TimeSpan Latency,
    string? RequestId,
    TimeSpan? RetryAfter,
    string? ErrorCode,
    string? StructuredJson,
    string? ModelRevision = null);
