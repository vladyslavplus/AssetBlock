namespace AssetBlock.Domain.Core.Dto.Users;

public record UpdateRecommendationPreferencesRequest
{
    public bool IsPersonalized { get; init; }
}

public record RecommendationPreferencesDto
{
    public bool IsPersonalized { get; init; }
    public DateTimeOffset? OptedInAt { get; init; }
}
