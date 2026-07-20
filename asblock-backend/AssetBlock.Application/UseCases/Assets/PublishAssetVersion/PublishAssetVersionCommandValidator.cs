using FluentValidation;

namespace AssetBlock.Application.UseCases.Assets.PublishAssetVersion;

internal sealed class PublishAssetVersionCommandValidator : AbstractValidator<PublishAssetVersionCommand>
{
    public PublishAssetVersionCommandValidator()
    {
        RuleFor(c => c.AssetId).NotEmpty().WithMessage("AssetId is required.");
        RuleFor(c => c.AuthorId).NotEmpty().WithMessage("AuthorId is required.");
        RuleFor(c => c.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(c => c.Request.LicenseCode)
                    .NotEmpty().WithMessage("LicenseCode is required.")
                    .MaximumLength(64).WithMessage("LicenseCode must not exceed 64 characters.");
                RuleFor(c => c.Request.ReleaseNotes)
                    .NotEmpty().WithMessage("ReleaseNotes are required.")
                    .MaximumLength(4000).WithMessage("ReleaseNotes must not exceed 4000 characters.")
                    .Must(notes => !string.IsNullOrWhiteSpace(notes)).WithMessage("ReleaseNotes are required.");
            });
        RuleFor(c => c.FileName)
            .NotEmpty().WithMessage("FileName is required.")
            .MaximumLength(512).WithMessage("FileName must not exceed 512 characters.");
        RuleFor(c => c.FileContent)
            .NotNull().WithMessage("File content is required.");
        RuleFor(c => c.FileLength)
            .GreaterThan(0).WithMessage("FileLength must be greater than zero.");
    }
}
