using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Options;

namespace AssetBlock.Infrastructure.Tests.OptionsValidatorTests;

public sealed class StripeOptionsValidatorTests
{
    private readonly StripeOptionsValidator _sut = new();

    [Fact]
    public void Validate_WhenAllFieldsEmpty_ShouldSucceed()
    {
        var result = _sut.Validate(null, new StripeOptions());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenFullyConfigured_ShouldSucceed()
    {
        var result = _sut.Validate(null, CreateValid());
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenPartiallyConfigured_ShouldFail()
    {
        var result = _sut.Validate(null, new StripeOptions
        {
            SecretKey = "stripe_test_secret_key_not_real"
        });

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("WebhookSecret"));
        result.Failures.Should().Contain(m => m.Contains("DefaultSuccessUrl"));
        result.Failures.Should().Contain(m => m.Contains("DefaultCancelUrl"));
    }

    [Fact]
    public void Validate_WhenAllFieldsArePlaceholders_ShouldSucceed()
    {
        var result = _sut.Validate(null, new StripeOptions
        {
            SecretKey = "<stripe-secret-key>",
            WebhookSecret = "<stripe-webhook-secret>",
            DefaultSuccessUrl = "<default-success-url>",
            DefaultCancelUrl = "<default-cancel-url>"
        });

        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Validate_WhenRedirectUrlsInvalid_ShouldFail()
    {
        var options = CreateValid();
        options.DefaultSuccessUrl = "not-a-url";
        options.DefaultCancelUrl = "/relative/cancel";

        var result = _sut.Validate(null, options);

        result.Failed.Should().BeTrue();
        result.Failures.Should().Contain(m => m.Contains("DefaultSuccessUrl"));
        result.Failures.Should().Contain(m => m.Contains("DefaultCancelUrl"));
    }

    private static StripeOptions CreateValid() => new()
    {
        SecretKey = "stripe_test_secret_key_not_real",
        WebhookSecret = "stripe_test_webhook_secret_not_real",
        DefaultSuccessUrl = "http://localhost:3000/payment/success",
        DefaultCancelUrl = "http://localhost:3000/payment/cancel"
    };
}
