namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class StripeOptions
{
    public const string SECTION_NAME = "Stripe";

    public string SecretKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
}
