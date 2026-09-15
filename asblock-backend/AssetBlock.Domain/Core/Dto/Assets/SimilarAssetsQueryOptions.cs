namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record SimilarAssetsQueryOptions(
    bool AllowSemanticRefinement,
    string? CanonicalModelKey,
    int ExpectedDimension,
    string? ContentSchemaVersion,
    bool UsePopularityRanking = false,
    Guid? PersonalUserId = null)
{
    public static SimilarAssetsQueryOptions MetadataOnly { get; } =
        new(false, null, 0, null);
}
