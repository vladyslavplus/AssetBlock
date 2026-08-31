using AssetBlock.Application.Common.Validators;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.UploadAsset;

internal sealed class UploadAssetCommandValidator : AbstractValidator<UploadAssetCommand>
{
    public UploadAssetCommandValidator(IOptions<FileUploadOptions> fileUploadOptions)
    {
        FileUploadOptions uploadOpts = fileUploadOptions.Value;

        RuleFor(c => c.AuthorId)
            .NotEmpty().WithMessage("AuthorId is required.");
        RuleFor(c => c.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(c => c.Request.Title)
                    .NotEmpty().WithMessage("Title is required.")
                    .MaximumLength(ListingSuggestionBounds.TITLE_MAX_LENGTH)
                    .WithMessage($"Title must not exceed {ListingSuggestionBounds.TITLE_MAX_LENGTH} characters.");
                RuleFor(c => c.Request.Price).MarketplacePrice();
                RuleFor(c => c.Request.CategoryId)
                    .NotEmpty().WithMessage("CategoryId is required.");
                RuleFor(c => c.Request.DownloadLimitPerHour)
                    .GreaterThan(0).When(c => c.Request.DownloadLimitPerHour.HasValue)
                    .WithMessage("DownloadLimitPerHour must be greater than zero when specified.");
                RuleFor(c => c.Request.Description)
                    .MaximumLength(ListingSuggestionBounds.DESCRIPTION_MAX_LENGTH)
                    .WithMessage($"Description must not exceed {ListingSuggestionBounds.DESCRIPTION_MAX_LENGTH} characters.")
                    .When(c => !string.IsNullOrEmpty(c.Request.Description));
                RuleFor(c => c.Request.LicenseCode)
                    .NotEmpty().WithMessage("LicenseCode is required.")
                    .MaximumLength(64).WithMessage("LicenseCode must not exceed 64 characters.")
                    .Must(code => AssetLicenseCatalog.TryParseCode(code, out _))
                    .WithMessage("LicenseCode is invalid.");
            });
        RuleFor(c => c.FileName)
            .NotEmpty().WithMessage("FileName is required.")
            .MaximumLength(512).WithMessage("FileName must not exceed 512 characters.")
            .Must(name => uploadOpts.TryMatchAllowedExtension(Path.GetFileName(name), out _))
            .WithMessage("File extension is not allowed.");
        RuleFor(c => c.FileContent)
            .NotNull().WithMessage("File content is required.");
        RuleFor(c => c.FileLength)
            .GreaterThan(0).WithMessage("FileLength must be greater than zero.")
            .LessThanOrEqualTo(uploadOpts.MaxFileBytes).WithMessage($"File size must not exceed {uploadOpts.MaxFileBytes} bytes.");
    }
}
