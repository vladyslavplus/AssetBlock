using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.EnqueueListingCopilot;

internal sealed class EnqueueListingCopilotCommandValidator : AbstractValidator<EnqueueListingCopilotCommand>
{
    public EnqueueListingCopilotCommandValidator()
    {
        RuleFor(x => x.AssetVersionId)
            .NotEmpty()
            .WithMessage("AssetVersionId is required.");

        RuleFor(x => x.OwnerUserId)
            .NotEmpty()
            .WithMessage("OwnerUserId is required.");
    }
}
