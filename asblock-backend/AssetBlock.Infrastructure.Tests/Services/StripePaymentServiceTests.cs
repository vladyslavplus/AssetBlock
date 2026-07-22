using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Polly;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class StripePaymentServiceTests
{
    [Fact]
    public async Task CreateCheckoutSession_throws_whenSuccessAndCancelUrlsMissing()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new StripeOptions
        {
            SecretKey = "stripe_test_secret_key_not_real",
            DefaultSuccessUrl = "",
            DefaultCancelUrl = ""
        });
        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>())
            .Returns(_ => new ResiliencePipelineBuilder().Build());

        var sut = new StripePaymentService(
            opts,
            resilience,
            NullLogger<StripePaymentService>.Instance);

        var lineItem = new CheckoutLineItem(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Test Asset",
            9.99m,
            "usd",
            DateTimeOffset.UtcNow.AddHours(1));
        var act = async () => await sut.CreateCheckoutSession(lineItem, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateCheckoutSession_failsFast_whenStripeApiUnreachable()
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new StripeOptions
        {
            SecretKey = "stripe_test_secret_key_not_real",
            DefaultSuccessUrl = "https://example.com/success",
            DefaultCancelUrl = "https://example.com/cancel"
        });

        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>())
            .Returns(_ => new ResiliencePipelineBuilder().Build());

        var sut = new StripePaymentService(
            opts,
            resilience,
            NullLogger<StripePaymentService>.Instance);

        var lineItem = new CheckoutLineItem(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Test Asset",
            9.99m,
            "usd",
            DateTimeOffset.UtcNow.AddHours(1));
        var act = async () => await sut.CreateCheckoutSession(lineItem, Guid.NewGuid());

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task VerifyCheckoutCompleted_throws_whenWebhookSecretMissing()
    {
        var sut = CreateSut(webhookSecret: "");
        var act = async () => await sut.VerifyCheckoutCompleted("{}", "sig");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task VerifyCheckoutCompleted_throwsInvalidSignature_whenPayloadInvalid()
    {
        var sut = CreateSut(webhookSecret: "stripe_test_webhook_secret_not_real");
        var act = async () => await sut.VerifyCheckoutCompleted("not-json", "bad_sig");
        await act.Should().ThrowAsync<StripeWebhookInvalidSignatureException>();
    }

    private static StripePaymentService CreateSut(string webhookSecret)
    {
        var opts = Microsoft.Extensions.Options.Options.Create(new StripeOptions
        {
            SecretKey = "stripe_test_secret_key_not_real",
            WebhookSecret = webhookSecret
        });
        var resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>())
            .Returns(_ => new ResiliencePipelineBuilder().Build());

        return new StripePaymentService(
            opts,
            resilience,
            NullLogger<StripePaymentService>.Instance);
    }
}
