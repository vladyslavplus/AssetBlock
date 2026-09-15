using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Application.UseCases.Users.UpdateRecommendationPreferences;

public sealed record UpdateRecommendationPreferencesCommand(
    Guid UserId,
    bool IsPersonalized) : IRequest<Result<RecommendationPreferencesDto>>;
