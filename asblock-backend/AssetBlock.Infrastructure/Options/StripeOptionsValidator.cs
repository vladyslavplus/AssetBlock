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
        var secretSet = !OptionsValidation.IsMissingOrPlaceholder(options.SecretKey);
        var webhookSet = !OptionsValidation.IsMissingOrPlaceholder(options.WebhookSecret);
        var successSet = !OptionsValidation.IsMissingOrPlaceholder(options.DefaultSuccessUrl);
        var cancelSet = !OptionsValidation.IsMissingOrPlaceholder(options.DefaultCancelUrl);

        if (!secretSet && !webhookSet && !successSet && !cancelSet)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();

        if (!secretSet)
        {
            failures.Add("Stripe:SecretKey is required when any Stripe setting is configured.");
        }

        if (!webhookSet)
        {
            failures.Add("Stripe:WebhookSecret is required when any Stripe setting is configured.");
        }

        if (!successSet)
        {
            failures.Add("Stripe:DefaultSuccessUrl is required when any Stripe setting is configured.");
        }
        else if (!OptionsValidation.IsAbsoluteHttpUri(options.DefaultSuccessUrl))
        {
            failures.Add("Stripe:DefaultSuccessUrl must be an absolute http or https URI.");
        }

        if (!cancelSet)
        {
            failures.Add("Stripe:DefaultCancelUrl is required when any Stripe setting is configured.");
        }
        else if (!OptionsValidation.IsAbsoluteHttpUri(options.DefaultCancelUrl))
        {
            failures.Add("Stripe:DefaultCancelUrl must be an absolute http or https URI.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
