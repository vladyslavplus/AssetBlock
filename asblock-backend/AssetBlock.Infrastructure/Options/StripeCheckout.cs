using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

namespace AssetBlock.Infrastructure.Options;

/// <summary>
/// Shared "Stripe checkout is usable" semantics for options validation and the capabilities API.
/// </summary>
public static class StripeCheckout
{
    /// <summary>
    /// True when secret, webhook secret, and both default redirect URLs are set (non-placeholder)
    /// and redirect URLs are absolute http(s).
    /// </summary>
    public static bool IsConfigured(StripeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return !OptionsValidation.IsMissingOrPlaceholder(options.SecretKey)
            && !OptionsValidation.IsMissingOrPlaceholder(options.WebhookSecret)
            && OptionsValidation.IsAbsoluteHttpUri(options.DefaultSuccessUrl)
            && OptionsValidation.IsAbsoluteHttpUri(options.DefaultCancelUrl);
    }

    /// <summary>
    /// True when any Stripe field is set to a real (non-placeholder) value.
    /// </summary>
    public static bool IsAnyFieldConfigured(StripeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return !OptionsValidation.IsMissingOrPlaceholder(options.SecretKey)
            || !OptionsValidation.IsMissingOrPlaceholder(options.WebhookSecret)
            || !OptionsValidation.IsMissingOrPlaceholder(options.DefaultSuccessUrl)
            || !OptionsValidation.IsMissingOrPlaceholder(options.DefaultCancelUrl);
    }
}
