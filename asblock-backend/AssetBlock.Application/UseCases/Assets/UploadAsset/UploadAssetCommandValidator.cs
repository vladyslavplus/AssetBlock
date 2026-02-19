using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.UploadAsset;

internal sealed class UploadAssetCommandValidator : AbstractValidator<UploadAssetCommand>
{
    public UploadAssetCommandValidator()
    {
        RuleFor(c => c.AuthorId)
            .NotEmpty().WithMessage("AuthorId is required.");
        RuleFor(c => c.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(c => c.Request.Title)
                    .NotEmpty().WithMessage("Title is required.")
                    .MaximumLength(500).WithMessage("Title must not exceed 500 characters.");
                RuleFor(c => c.Request.Price)
                    .GreaterThan(0).WithMessage("Price must be greater than zero.");
                RuleFor(c => c.Request.CategoryId)
                    .NotEmpty().WithMessage("CategoryId is required.");
                RuleFor(c => c.Request.DownloadLimitPerHour)
                    .GreaterThan(0).When(c => c.Request.DownloadLimitPerHour.HasValue)
                    .WithMessage("DownloadLimitPerHour must be greater than zero when specified.");
            });
        RuleFor(c => c.FileName)
            .NotEmpty().WithMessage("FileName is required.")
            .MaximumLength(512).WithMessage("FileName must not exceed 512 characters.");
        RuleFor(c => c.FileContent)
            .NotNull().WithMessage("File content is required.");
    }
}
