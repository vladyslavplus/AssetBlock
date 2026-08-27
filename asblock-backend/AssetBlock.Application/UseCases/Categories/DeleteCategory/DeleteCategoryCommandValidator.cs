using FluentValidation;

namespace AssetBlock.Application.UseCases.Categories.DeleteCategory;

internal sealed class DeleteCategoryCommandValidator : AbstractValidator<DeleteCategoryCommand>
{
    public DeleteCategoryCommandValidator()
    {
        RuleFor(c => c.Id)
            .NotEmpty().WithMessage("Id is required.");
    }
}
