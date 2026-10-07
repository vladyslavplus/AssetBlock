using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using FluentValidation;
using Microsoft.Extensions.Options;

namespace AssetBlock.Application.UseCases.Assets.PublishAssetVersion;

internal sealed class PublishAssetVersionCommandValidator : AbstractValidator<PublishAssetVersionCommand>
{
    public PublishAssetVersionCommandValidator(IOptions<FileUploadOptions> fileUploadOptions)
    {
        FileUploadOptions uploadOpts = fileUploadOptions.Value;

        RuleFor(c => c.AssetId).NotEmpty().WithMessage("AssetId is required.");
        RuleFor(c => c.AuthorId).NotEmpty().WithMessage("AuthorId is required.");
        RuleFor(c => c.Request)
            .NotNull().WithMessage("Request is required.")
            .DependentRules(() =>
            {
                RuleFor(c => c.Request.LicenseCode)
                    .NotEmpty().WithMessage("LicenseCode is required.")
                    .MaximumLength(64).WithMessage("LicenseCode must not exceed 64 characters.")
                    .Must(code => AssetLicenseCatalog.TryParseCode(code, out _))
                    .WithMessage("LicenseCode is invalid.");
                RuleFor(c => c.Request.WorkspaceId)
                    .NotEmpty().WithMessage("WorkspaceId must be a non-empty UUID when specified.")
                    .When(c => c.Request.WorkspaceId.HasValue);
                RuleFor(c => c.Request)
                    .Must(r => r.WorkspaceId.HasValue == r.ExpectedWorkspaceRevision.HasValue)
                    .WithMessage("WorkspaceId and ExpectedWorkspaceRevision must be provided together.")
                    .WithName("Request");
                RuleFor(c => c.Request.ExpectedWorkspaceRevision)
                    .GreaterThan(0).WithMessage("ExpectedWorkspaceRevision must be greater than zero when specified.")
                    .When(c => c.Request.ExpectedWorkspaceRevision.HasValue);
                RuleFor(c => c.Request.ReleaseNotes)
                    .Cascade(CascadeMode.Stop)
                    .Must(notes => !string.IsNullOrWhiteSpace(notes)).WithMessage("ReleaseNotes are required.")
                    .Must(notes => notes!.Trim().Length <= 4000)
                    .WithMessage("ReleaseNotes must not exceed 4000 characters.");
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
