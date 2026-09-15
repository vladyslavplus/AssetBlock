namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>
/// Maps actually-used ranking evidence to one deterministic factual explanation per
/// candidate. Priority is fixed: personal click, personal tag affinity, popularity
/// signal, shared tags, same-category fallback. Explanations never influence ranking.
/// </summary>
public static class SimilarAssetsExplanations
{
    public static SimilarAssetExplanation Build(Guid assetId, SimilarCandidateEvidence evidence)
    {
        if (evidence.HasPersonalClick)
        {
            return new SimilarAssetExplanation(
                assetId,
                SimilarAssetsExplanationCodes.PERSONAL_RECOMMENDATION_CHOICES,
                "Based on your recommendation choices.");
        }

        if (evidence.HasPersonalTagScore)
        {
            return new SimilarAssetExplanation(
                assetId,
                SimilarAssetsExplanationCodes.PERSONAL_TAG_INTERESTS,
                "Matches tags from assets you purchased or reviewed.");
        }

        if (evidence.HasPopularitySignal)
        {
            return new SimilarAssetExplanation(
                assetId,
                SimilarAssetsExplanationCodes.POPULAR_IN_CATEGORY,
                "Popular in this category.");
        }

        if (evidence.SharedTagCount > 0)
        {
            return new SimilarAssetExplanation(
                assetId,
                SimilarAssetsExplanationCodes.SHARED_TAGS,
                evidence.SharedTagCount == 1
                    ? "Shares 1 tag with this asset."
                    : $"Shares {evidence.SharedTagCount} tags with this asset.");
        }

        return new SimilarAssetExplanation(
            assetId,
            SimilarAssetsExplanationCodes.SAME_CATEGORY,
            "Similar asset in the same category.");
    }

    public static SimilarCandidateEvidence Fallback() => new(false, false, false, 0);
}
