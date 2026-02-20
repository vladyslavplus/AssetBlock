using FluentValidation;

namespace AssetBlock.Application.UseCases.Categories.UpdateCategory;

internal sealed class UpdateCategoryCommandValidator : AbstractValidator<UpdateCategoryCommand>
{
    public UpdateCategoryCommandValidator()
    {
        RuleFor(c => c.Name)
            .NotEmpty().WithMessage("Name cannot be empty when provided.")
            .MaximumLength(255).WithMessage("Name must not exceed 255 characters.")
            .When(c => c.Name is not null);

        RuleFor(c => c.Slug)
            .NotEmpty().WithMessage("Slug cannot be empty when provided.")
            .Matches("^[a-z0-9]+(-[a-z0-9]+)*$").WithMessage("Slug must start and end with alphanumeric characters, with single hyphens between segments.")
            .MaximumLength(255).WithMessage("Slug must not exceed 255 characters.")
            .When(c => c.Slug is not null);

        RuleFor(c => c.Description)
            .MaximumLength(1000).WithMessage("Description must not exceed 1000 characters.")
            .When(c => c.Description is not null);
    }
}
