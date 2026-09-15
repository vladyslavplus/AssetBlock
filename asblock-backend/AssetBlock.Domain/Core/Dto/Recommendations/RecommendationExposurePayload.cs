namespace AssetBlock.Domain.Core.Dto.Recommendations;

public sealed record RecommendationExposurePayload(
    Guid ExposureId,
    Guid SourceAssetId,
    string RankingVersion,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<Guid> CandidateIds,
    Guid? AudienceUserId = null);
