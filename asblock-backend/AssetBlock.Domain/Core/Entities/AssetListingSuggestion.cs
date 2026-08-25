using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

public class AssetListingSuggestion
{
    public required Guid JobId { get; init; }
    public required string PromptPolicyVersion { get; set; }
    public required AiProviderKind Provider { get; set; }
    public required string ModelId { get; set; }
    public string? ModelRevision { get; set; }
    public string? UpstreamProvider { get; set; }
    public string? ProviderRequestId { get; set; }
    public required string Title { get; set; }
    public required string Description { get; set; }
    public required string Category { get; set; }
    public required string Tags { get; set; }
    public required string ContentHash { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }

    public AssetProcessingJob Job { get; set; } = null!;
}
