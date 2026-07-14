using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

/// <summary>
/// Stripe is optional: all fields empty or placeholder is valid (payments inactive).
/// If any Stripe field is set, every required field must be set and redirect URLs must be absolute http(s).
/// </summary>
internal sealed class StripeOptionsValidator : IValidateOptions<StripeOptions>
{
    public ValidateOptionsResult Validate(string? name, StripeOptions options)
    {
        if (!StripeCheckout.IsAnyFieldConfigured(options))
        {
            return ValidateOptionsResult.Success;
        }

        if (StripeCheckout.IsConfigured(options))
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (OptionsValidation.IsMissingOrPlaceholder(options.SecretKey))
        {
            failures.Add("Stripe:SecretKey is required when any Stripe setting is configured.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.WebhookSecret))
        {
            failures.Add("Stripe:WebhookSecret is required when any Stripe setting is configured.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.DefaultSuccessUrl))
        {
            failures.Add("Stripe:DefaultSuccessUrl is required when any Stripe setting is configured.");
        }
        else if (!OptionsValidation.IsAbsoluteHttpUri(options.DefaultSuccessUrl))
        {
            failures.Add("Stripe:DefaultSuccessUrl must be an absolute http or https URI.");
        }

        if (OptionsValidation.IsMissingOrPlaceholder(options.DefaultCancelUrl))
        {
            failures.Add("Stripe:DefaultCancelUrl is required when any Stripe setting is configured.");
        }
        else if (!OptionsValidation.IsAbsoluteHttpUri(options.DefaultCancelUrl))
        {
            failures.Add("Stripe:DefaultCancelUrl must be an absolute http or https URI.");
        }

        return ValidateOptionsResult.Fail(failures);
    }
}
