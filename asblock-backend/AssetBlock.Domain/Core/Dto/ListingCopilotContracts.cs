using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto;

public sealed record ListingCopilotOwnedVersion(
    Guid AssetId,
    Guid AssetVersionId,
    AssetVersionProcessingStatus ProcessingStatus,
    bool HasArchiveAnalysis,
    string FileName);

public sealed record ListingCopilotSuggestionWrite(
    Guid JobId,
    string PromptPolicyVersion,
    AiProviderKind Provider,
    string ModelId,
    string? ModelRevision,
    string? UpstreamProvider,
    string? ProviderRequestId,
    string Title,
    string Description,
    string Category,
    IReadOnlyList<string> Tags,
    string ContentHash,
    int? InputTokens,
    int? OutputTokens);

public sealed record ListingCopilotSuggestionDto(
    Guid JobId,
    Guid AssetVersionId,
    string Title,
    string Description,
    string Category,
    IReadOnlyList<string> Tags,
    AiProviderKind Provider,
    string ActualModel,
    string? ModelRevision,
    string? UpstreamProvider,
    DateTimeOffset CreatedAt);

public sealed record ListingCopilotEnqueueResponse(Guid JobId, Guid AssetVersionId);
