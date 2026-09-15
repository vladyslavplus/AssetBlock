using FluentValidation;

namespace AssetBlock.Application.UseCases.Users.GetRecommendationPreferences;

internal sealed class GetRecommendationPreferencesQueryValidator : AbstractValidator<GetRecommendationPreferencesQuery>
{
    public GetRecommendationPreferencesQueryValidator()
    {
        RuleFor(q => q.UserId)
            .NotEmpty().WithMessage("UserId is required.");
    }
}
