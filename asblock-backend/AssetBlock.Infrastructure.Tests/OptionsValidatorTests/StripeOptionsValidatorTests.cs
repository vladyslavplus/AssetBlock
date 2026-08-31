using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class StripeOptionsValidatorTests
{
    private readonly StripeOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenAllFieldsEmpty_ShouldSucceed()
    {
        ValidateOptionsResult result = _sut.Validate(null, new StripeOptions());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenFullyConfigured_ShouldSucceed()
    {
        ValidateOptionsResult result = _sut.Validate(null, CreateValid());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPartiallyConfigured_ShouldFail()
    {
        ValidateOptionsResult result = _sut.Validate(null, new StripeOptions
        {
            SecretKey = "stripe_test_secret_key_not_real"
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("WebhookSecret"));
        result.Failures.Should().Contain(m => m.Contains("SuccessUrl"));
        result.Failures.Should().Contain(m => m.Contains("CancelUrl"));
    }

    [Fact]
    public void Validate_WhenAllFieldsArePlaceholders_ShouldSucceed()
    {
        ValidateOptionsResult result = _sut.Validate(null, new StripeOptions
        {
            SecretKey = "<stripe-secret-key>",
            WebhookSecret = "<stripe-webhook-secret>",
            SuccessUrl = "<default-success-url>",
            CancelUrl = "<default-cancel-url>"
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRedirectUrlsInvalid_ShouldFail()
    {
        StripeOptions options = CreateValid();
        options.SuccessUrl = "not-a-url";
        options.CancelUrl = "/relative/cancel";

        ValidateOptionsResult result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("SuccessUrl"));
        result.Failures.Should().Contain(m => m.Contains("CancelUrl"));
    }

    private static StripeOptions CreateValid() => new()
    {
        SecretKey = "stripe_test_secret_key_not_real",
        WebhookSecret = "stripe_test_webhook_secret_not_real",
        SuccessUrl = "http://localhost:3000/checkout/success",
        CancelUrl = "http://localhost:3000/checkout/cancel"
    };
}
