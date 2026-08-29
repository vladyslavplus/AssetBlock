using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.GetListingCopilotSuggestion;

internal sealed class GetListingCopilotSuggestionQueryValidator : AbstractValidator<GetListingCopilotSuggestionQuery>
{
    public GetListingCopilotSuggestionQueryValidator()
    {
        RuleFor(x => x.AssetVersionId)
            .NotEmpty()
            .WithMessage("AssetVersionId is required.");

        RuleFor(x => x.OwnerUserId)
            .NotEmpty()
            .WithMessage("OwnerUserId is required.");
    }
}
