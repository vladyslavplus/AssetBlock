using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Payments;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Polly;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Tests.Services;

public sealed class StripePaymentServiceTests
{
    [Fact]
    public async Task CreateCheckoutSession_throws_whenSuccessAndCancelUrlsMissing()
    {
        IOptions<StripeOptions> opts = Microsoft.Extensions.Options.Options.Create(new StripeOptions
        {
            SecretKey = "stripe_test_secret_key_not_real",
            SuccessUrl = "",
            CancelUrl = ""
        });
        ResiliencePipelineProvider<string> resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>())
            .Returns(_ => new ResiliencePipelineBuilder().Build());

        var sut = new StripePaymentService(
            opts,
            resilience,
            NullLogger<StripePaymentService>.Instance);

        CheckoutSessionDraft draft = CreateDraft();
        Func<Task<StripeCheckoutSession>> act = async () => await sut.CreateCheckoutSession(draft);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateCheckoutSession_failsFast_whenStripeApiUnreachable()
    {
        IOptions<StripeOptions> opts = Microsoft.Extensions.Options.Options.Create(new StripeOptions
        {
            SecretKey = "stripe_test_secret_key_not_real",
            SuccessUrl = "https://example.com/checkout/success",
            CancelUrl = "https://example.com/checkout/cancel"
        });

        ResiliencePipelineProvider<string> resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>())
            .Returns(_ => new ResiliencePipelineBuilder().Build());

        var sut = new StripePaymentService(
            opts,
            resilience,
            NullLogger<StripePaymentService>.Instance);

        CheckoutSessionDraft draft = CreateDraft();
        Func<Task<StripeCheckoutSession>> act = async () => await sut.CreateCheckoutSession(draft);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task CreateCheckoutSession_throws_whenLineAmountHasSubCentPrecision()
    {
        StripePaymentService sut = CreateSut(webhookSecret: "whsec_test");
        var draft = new CheckoutSessionDraft(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(1),
            "usd",
            [new CheckoutSessionDraftLine("Test Asset", 9.999m, "usd")]);

        Func<Task<StripeCheckoutSession>> act = async () => await sut.CreateCheckoutSession(draft);

        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*at most two decimal places*");
    }

    [Fact]
    public async Task CreateCheckoutSession_throws_whenLineAmountIsZero()
    {
        StripePaymentService sut = CreateSut(webhookSecret: "whsec_test");
        var draft = new CheckoutSessionDraft(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(1),
            "usd",
            [new CheckoutSessionDraftLine("Test Asset", 0m, "usd")]);

        Func<Task<StripeCheckoutSession>> act = async () => await sut.CreateCheckoutSession(draft);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task CreateCheckoutSession_throws_whenLineAmountExceedsMaxAmount()
    {
        StripePaymentService sut = CreateSut(webhookSecret: "whsec_test");
        var draft = new CheckoutSessionDraft(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(1),
            "usd",
            [new CheckoutSessionDraftLine("Test Asset", 1_000_000.00m, "usd")]);

        Func<Task<StripeCheckoutSession>> act = async () => await sut.CreateCheckoutSession(draft);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage($"*{BundlePriceAllocator.MAX_AMOUNT_CENTS}*");
    }

    [Fact]
    public async Task VerifyCheckoutCompleted_throws_whenWebhookSecretMissing()
    {
        StripePaymentService sut = CreateSut(webhookSecret: "");
        Func<Task<StripeCheckoutCompleted?>> act = async () => await sut.VerifyCheckoutCompleted("{}", "sig");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task VerifyCheckoutCompleted_throwsInvalidSignature_whenPayloadInvalid()
    {
        StripePaymentService sut = CreateSut(webhookSecret: "stripe_test_webhook_secret_not_real");
        Func<Task<StripeCheckoutCompleted?>> act = async () => await sut.VerifyCheckoutCompleted("not-json", "bad_sig");
        await act.Should().ThrowAsync<StripeWebhookInvalidSignatureException>();
    }

    private static CheckoutSessionDraft CreateDraft() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow.AddHours(1),
            "usd",
            [new CheckoutSessionDraftLine("Test Asset", 9.99m, "usd")]);

    private static StripePaymentService CreateSut(string webhookSecret)
    {
        IOptions<StripeOptions> opts = Microsoft.Extensions.Options.Options.Create(new StripeOptions
        {
            SecretKey = "stripe_test_secret_key_not_real",
            WebhookSecret = webhookSecret,
            SuccessUrl = "https://example.com/checkout/success",
            CancelUrl = "https://example.com/checkout/cancel"
        });
        ResiliencePipelineProvider<string> resilience = Substitute.For<ResiliencePipelineProvider<string>>();
        resilience.GetPipeline(Arg.Any<string>())
            .Returns(_ => new ResiliencePipelineBuilder().Build());
        return new StripePaymentService(opts, resilience, NullLogger<StripePaymentService>.Instance);
    }
}
