using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
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
using Stripe;

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

    [Fact]
    public async Task VerifyCheckoutCompleted_mapsValidPaidSession_withUsdCentsConversion()
    {
        const string secret = "whsec_test_valid_secret";
        StripePaymentService sut = CreateSut(webhookSecret: secret);
        var expectedUserId = Guid.NewGuid();
        var expectedIntentId = Guid.NewGuid();
        const string sessionId = "cs_test_session_12345";

        (var payload, var signature) = CreateSignedWebhookEvent(
            sessionId: sessionId,
            amountTotal: 4999,
            currency: "usd",
            userId: expectedUserId.ToString(),
            checkoutIntentId: expectedIntentId.ToString(),
            webhookSecret: secret);

        StripeCheckoutCompleted? result = await sut.VerifyCheckoutCompleted(payload, signature);

        result.Should().NotBeNull();
        result.CheckoutIntentId.Should().Be(expectedIntentId);
        result.UserId.Should().Be(expectedUserId);
        result.StripeSessionId.Should().Be(sessionId);
        result.AmountTotal.Should().Be(49.99m);
        result.Currency.Should().Be("usd");
    }

    [Theory]
    [InlineData(1, 0.01)]
    [InlineData(100, 1.00)]
    [InlineData(99999999, 999999.99)]
    public async Task VerifyCheckoutCompleted_handlesBoundaryCents_correctly(long cents, decimal expectedDollars)
    {
        const string secret = "whsec_test_boundary_secret";
        StripePaymentService sut = CreateSut(webhookSecret: secret);

        (var payload, var signature) = CreateSignedWebhookEvent(
            amountTotal: cents,
            currency: "USD",
            webhookSecret: secret);

        StripeCheckoutCompleted? result = await sut.VerifyCheckoutCompleted(payload, signature);

        result.Should().NotBeNull();
        result.AmountTotal.Should().Be(expectedDollars);
        result.Currency.Should().Be("usd");
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-100L)]
    public async Task VerifyCheckoutCompleted_throwsInvalidOperationException_whenAmountTotalIsMissingOrNonPositive(long? amountTotal)
    {
        const string secret = "whsec_test_invalid_amount";
        StripePaymentService sut = CreateSut(webhookSecret: secret);

        (var payload, var signature) = CreateSignedWebhookEvent(
            amountTotal: amountTotal,
            currency: "usd",
            webhookSecret: secret);

        Func<Task<StripeCheckoutCompleted?>> act = async () => await sut.VerifyCheckoutCompleted(payload, signature);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid amount or currency*");
    }

    [Theory]
    [InlineData("eur")]
    [InlineData("gbp")]
    [InlineData("jpy")]
    [InlineData("")]
    public async Task VerifyCheckoutCompleted_throwsInvalidOperationException_whenCurrencyIsNotUsd(string? currency)
    {
        const string secret = "whsec_test_non_usd";
        StripePaymentService sut = CreateSut(webhookSecret: secret);

        (var payload, var signature) = CreateSignedWebhookEvent(
            amountTotal: 1000,
            currency: currency,
            webhookSecret: secret);

        Func<Task<StripeCheckoutCompleted?>> act = async () => await sut.VerifyCheckoutCompleted(payload, signature);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid amount or currency*");
    }

    [Fact]
    public async Task VerifyCheckoutCompleted_returnsNull_whenMetadataIsMissingOrInvalid()
    {
        const string secret = "whsec_test_bad_metadata";
        StripePaymentService sut = CreateSut(webhookSecret: secret);

        // Missing checkout_intent_id
        (var payload1, var signature1) = CreateSignedWebhookEvent(
            checkoutIntentId: null,
            webhookSecret: secret);
        StripeCheckoutCompleted? result1 = await sut.VerifyCheckoutCompleted(payload1, signature1);
        result1.Should().BeNull();

        // Non-GUID user_id
        (var payload2, var signature2) = CreateSignedWebhookEvent(
            userId: "not-a-valid-guid",
            webhookSecret: secret);
        StripeCheckoutCompleted? result2 = await sut.VerifyCheckoutCompleted(payload2, signature2);
        result2.Should().BeNull();

        // Empty metadata dictionary
        (var payload3, var signature3) = CreateSignedWebhookEvent(
            customMetadata: new Dictionary<string, string>(),
            webhookSecret: secret);
        StripeCheckoutCompleted? result3 = await sut.VerifyCheckoutCompleted(payload3, signature3);
        result3.Should().BeNull();
    }

    [Fact]
    public async Task VerifyCheckoutCompleted_returnsNull_whenPaymentStatusIsNotPaid()
    {
        const string secret = "whsec_test_unpaid";
        StripePaymentService sut = CreateSut(webhookSecret: secret);

        (var payload, var signature) = CreateSignedWebhookEvent(
            paymentStatus: "unpaid",
            webhookSecret: secret);

        StripeCheckoutCompleted? result = await sut.VerifyCheckoutCompleted(payload, signature);

        result.Should().BeNull();
    }

    [Fact]
    public async Task VerifyCheckoutCompleted_returnsNull_whenEventTypeIsNotCheckoutSessionCompleted()
    {
        const string secret = "whsec_test_wrong_event";
        StripePaymentService sut = CreateSut(webhookSecret: secret);

        (var payload, var signature) = CreateSignedWebhookEvent(
            eventType: "payment_intent.succeeded",
            webhookSecret: secret);

        StripeCheckoutCompleted? result = await sut.VerifyCheckoutCompleted(payload, signature);

        result.Should().BeNull();
    }

    private static (string Payload, string Signature) CreateSignedWebhookEvent(
        string eventType = "checkout.session.completed",
        string sessionId = "cs_test_123",
        string paymentStatus = "paid",
        long? amountTotal = 4999,
        string? currency = "usd",
        string? userId = "11111111-1111-1111-1111-111111111111",
        string? checkoutIntentId = "22222222-2222-2222-2222-222222222222",
        Dictionary<string, string>? customMetadata = null,
        string webhookSecret = "whsec_test")
    {
        Dictionary<string, string> metadata = customMetadata ?? new Dictionary<string, string>();
        if (customMetadata is null)
        {
            if (userId is not null)
            {
                metadata[StripeConstants.MetadataKeys.USER_ID] = userId;
            }

            if (checkoutIntentId is not null)
            {
                metadata[StripeConstants.MetadataKeys.CHECKOUT_INTENT_ID] = checkoutIntentId;
            }
        }

        var session = new Stripe.Checkout.Session
        {
            Id = sessionId,
            Object = "checkout.session",
            PaymentStatus = paymentStatus,
            AmountTotal = amountTotal,
            Currency = currency,
            Metadata = metadata
        };

        var eventObj = new Event
        {
            Id = "evt_test_123",
            Type = eventType,
            ApiVersion = StripeConfiguration.ApiVersion,
            Created = DateTime.UtcNow,
            Data = new EventData
            {
                Object = session
            }
        };

        var payload = eventObj.ToJson();
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var signature = $"t={timestamp},v1={EventUtility.ComputeSignature(webhookSecret, timestamp.ToString(), payload)}";

        return (payload, signature);
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
