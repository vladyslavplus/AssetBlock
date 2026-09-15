using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Application.UseCases.Users.GetRecommendationPreferences;

public sealed record GetRecommendationPreferencesQuery(
    Guid UserId) : IRequest<Result<RecommendationPreferencesDto>>;
