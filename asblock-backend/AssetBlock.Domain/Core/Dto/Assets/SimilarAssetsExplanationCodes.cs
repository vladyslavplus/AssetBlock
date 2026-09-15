namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>
/// Deterministic factual reason codes for similar-asset explanations.
/// Codes are stable API surface; texts are fixed templates without free-text interpolation.
/// </summary>
public static class SimilarAssetsExplanationCodes
{
    public const string PERSONAL_RECOMMENDATION_CHOICES = "PERSONAL_RECOMMENDATION_CHOICES";
    public const string PERSONAL_TAG_INTERESTS = "PERSONAL_TAG_INTERESTS";
    public const string POPULAR_IN_CATEGORY = "POPULAR_IN_CATEGORY";
    public const string SHARED_TAGS = "SHARED_TAGS";
    public const string SAME_CATEGORY = "SAME_CATEGORY";
}
