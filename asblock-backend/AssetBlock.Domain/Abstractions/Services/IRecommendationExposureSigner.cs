using AssetBlock.Domain.Core.Dto.Recommendations;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>HMAC for similar-assets exposure context. Does not encrypt candidate IDs.</summary>
public interface IRecommendationExposureSigner
{
    string? TryCreateToken(RecommendationExposurePayload payload);

    bool TryVerify(RecommendationExposurePayload payload, string token);
}
