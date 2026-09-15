namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record SimilarAssetsResult(
    IReadOnlyList<AssetListItem> Items,
    SimilarAssetsExposure? Exposure,
    IReadOnlyList<SimilarAssetExplanation> Explanations);
