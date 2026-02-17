using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.UploadAsset;

internal sealed class UploadAssetCommandValidator : AbstractValidator<UploadAssetCommand>
{
    public UploadAssetCommandValidator()
    {
        RuleFor(c => c.AuthorId)
            .NotEmpty().WithMessage("AuthorId is required.");
        RuleFor(c => c.Request)
            .NotNull().WithMessage("Request is required.");
        When(_ => true, () =>
        {
            RuleFor(c => c.Request.Title)
                .NotEmpty().WithMessage("Title is required.")
                .MaximumLength(500).WithMessage("Title must not exceed 500 characters.");
            RuleFor(c => c.Request.Price)
                .GreaterThan(0).WithMessage("Price must be greater than zero.");
            RuleFor(c => c.Request.CategoryId)
                .NotEmpty().WithMessage("CategoryId is required.");
        });
        RuleFor(c => c.FileName)
            .NotEmpty().WithMessage("FileName is required.")
            .MaximumLength(255).WithMessage("FileName must not exceed 255 characters.");
        RuleFor(c => c.FileContent)
            .NotNull().WithMessage("File content is required.");
    }
}
