using AssetBlock.Application.UseCases.Tags.CreateTag;
using FluentValidation;

namespace AssetBlock.Application.Validators.Tags;

public sealed class CreateTagCommandValidator : AbstractValidator<CreateTagCommand>
{
    public CreateTagCommandValidator()
    {
        RuleFor(x => x.Name)
            .NotEmpty().WithMessage("Tag name is required.")
            .MaximumLength(50).WithMessage("Tag name maximum length is 50 characters.")
            .Matches("^[a-z0-9]+(-[a-z0-9]+)*$").WithMessage("Tag name must start and end with lowercase alphanumeric characters, with single hyphens between segments.");
    }
}
