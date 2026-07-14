using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class StripeCheckoutTests
{
    [Fact]
    public void IsConfigured_WhenAllPlaceholders_ShouldBeFalse()
    {
        var options = new StripeOptions
        {
            SecretKey = "<stripe-secret-key>",
            WebhookSecret = "<stripe-webhook-secret>",
            DefaultSuccessUrl = "<default-success-url>",
            DefaultCancelUrl = "<default-cancel-url>"
        };

        StripeCheckout.IsConfigured(options).Should().BeFalse();
        StripeCheckout.IsAnyFieldConfigured(options).Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WhenEmpty_ShouldBeFalse()
    {
        StripeCheckout.IsConfigured(new StripeOptions()).Should().BeFalse();
    }

    [Fact]
    public void IsConfigured_WhenFullyConfigured_ShouldBeTrue()
    {
        var options = new StripeOptions
        {
            SecretKey = "stripe_test_secret_key_not_real",
            WebhookSecret = "stripe_test_webhook_secret_not_real",
            DefaultSuccessUrl = "http://localhost:3000/payment/success",
            DefaultCancelUrl = "http://localhost:3000/payment/cancel"
        };

        StripeCheckout.IsConfigured(options).Should().BeTrue();
    }

    [Fact]
    public void IsConfigured_WhenUrlsRelative_ShouldBeFalse()
    {
        var options = new StripeOptions
        {
            SecretKey = "stripe_test_secret_key_not_real",
            WebhookSecret = "stripe_test_webhook_secret_not_real",
            DefaultSuccessUrl = "/payment/success",
            DefaultCancelUrl = "/payment/cancel"
        };

        StripeCheckout.IsConfigured(options).Should().BeFalse();
        StripeCheckout.IsAnyFieldConfigured(options).Should().BeTrue();
    }
}
