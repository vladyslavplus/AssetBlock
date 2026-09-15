using FluentValidation;

namespace AssetBlock.Application.UseCases.Users.UpdateRecommendationPreferences;

internal sealed class UpdateRecommendationPreferencesCommandValidator : AbstractValidator<UpdateRecommendationPreferencesCommand>
{
    public UpdateRecommendationPreferencesCommandValidator()
    {
        RuleFor(c => c.UserId)
            .NotEmpty().WithMessage("UserId is required.");
    }
}
