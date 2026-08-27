using AssetBlock.Application.UseCases.Tags.CreateTag;
using AssetBlock.Domain.Core.Constants;
using FluentValidation;

namespace AssetBlock.Application.Validators.Tags;

internal sealed class CreateTagCommandValidator : AbstractValidator<CreateTagCommand>
{
    public CreateTagCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Tag name is required.")
            .MaximumLength(TagConstants.NAME_MAX_LENGTH).WithMessage($"Tag name maximum length is {TagConstants.NAME_MAX_LENGTH} characters.")
            .Matches(TagConstants.SLUG_PATTERN).WithMessage("Tag name must start and end with lowercase alphanumeric characters, with single hyphens between segments.");
    }
}
