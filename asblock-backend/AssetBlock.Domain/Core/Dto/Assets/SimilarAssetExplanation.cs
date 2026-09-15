namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>
/// One deterministic factual explanation for a returned similar-asset candidate.
/// Text is a fixed template; only the shared-tag count is interpolated (never titles,
/// descriptions, tag names, scores, counts, vectors, or history references).
/// </summary>
public sealed record SimilarAssetExplanation(
    Guid AssetId,
    string Code,
    string Text);
