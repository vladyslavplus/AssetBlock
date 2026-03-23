using FluentValidation;

namespace AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;

internal sealed class CreateCheckoutSessionCommandValidator : AbstractValidator<CreateCheckoutSessionCommand>
{
    public CreateCheckoutSessionCommandValidator()
    {
        RuleFor(c => c.AssetId)
            .NotEmpty().WithMessage("AssetId is required.");

        RuleFor(c => c.SuccessUrl)
            .Cascade(CascadeMode.Stop)
            .Must(url => string.IsNullOrWhiteSpace(url) || BeAbsoluteHttpsUrl(url!))
            .WithMessage("SuccessUrl must be an absolute HTTPS URL.");

        RuleFor(c => c.CancelUrl)
            .Cascade(CascadeMode.Stop)
            .Must(url => string.IsNullOrWhiteSpace(url) || BeAbsoluteHttpsUrl(url!))
            .WithMessage("CancelUrl must be an absolute HTTPS URL.");
    }

    private static bool BeAbsoluteHttpsUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
               && uri is { Scheme: "https", Host.Length: > 0 };
    }
}
