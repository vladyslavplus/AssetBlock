using FluentValidation;

namespace AssetBlock.Application.UseCases.Bundles.ArchiveBundle;

internal sealed class ArchiveBundleCommandValidator : AbstractValidator<ArchiveBundleCommand>
{
    public ArchiveBundleCommandValidator()
    {
        RuleFor(c => c.BundleId)
            .NotEmpty().WithMessage("BundleId is required.");
        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");
    }
}
