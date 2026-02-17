using FluentValidation;

namespace AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;

internal sealed class CreateCheckoutSessionCommandValidator : AbstractValidator<CreateCheckoutSessionCommand>
{
    public CreateCheckoutSessionCommandValidator()
    {
        RuleFor(c => c.AssetId)
            .NotEmpty().WithMessage("AssetId is required.");

        RuleFor(c => c.SuccessUrl)
            .NotEmpty().WithMessage("SuccessUrl is required.")
            .Must(BeAbsoluteHttpsUrl).WithMessage("SuccessUrl must be an absolute HTTPS URL.");

        RuleFor(c => c.CancelUrl)
            .NotEmpty().WithMessage("CancelUrl is required.")
            .Must(BeAbsoluteHttpsUrl).WithMessage("CancelUrl must be an absolute HTTPS URL.");
    }

    private static bool BeAbsoluteHttpsUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == "https")
            && (uri.Host?.Length > 0);
    }
}
