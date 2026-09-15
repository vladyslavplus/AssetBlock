using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Application.UseCases.Users.GetRecommendationPreferences;

internal sealed class GetRecommendationPreferencesQueryHandler(
    IRecommendationPersonalizationStore personalizationStore)
    : IRequestHandler<GetRecommendationPreferencesQuery, Result<RecommendationPreferencesDto>>
{
    public async Task<Result<RecommendationPreferencesDto>> Handle(
        GetRecommendationPreferencesQuery request,
        CancellationToken cancellationToken)
    {
        // Missing preference row means opted out (default-off, never inferred).
        UserRecommendationPreferences? preferences =
            await personalizationStore.GetPreferences(request.UserId, cancellationToken);

        return Result.Success(new RecommendationPreferencesDto
        {
            IsPersonalized = preferences?.IsPersonalized ?? false,
            OptedInAt = preferences?.OptedInAt
        });
    }
}
