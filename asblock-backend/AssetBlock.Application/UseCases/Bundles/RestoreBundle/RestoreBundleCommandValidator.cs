using FluentValidation;

namespace AssetBlock.Application.UseCases.Bundles.RestoreBundle;

internal sealed class RestoreBundleCommandValidator : AbstractValidator<RestoreBundleCommand>
{
    public RestoreBundleCommandValidator()
    {
        RuleFor(c => c.BundleId)
            .NotEmpty().WithMessage("BundleId is required.");
        RuleFor(c => c.SellerId)
            .NotEmpty().WithMessage("SellerId is required.");
    }
}
