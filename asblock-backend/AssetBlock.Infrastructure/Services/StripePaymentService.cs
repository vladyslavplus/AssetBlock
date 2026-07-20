using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly.Registry;
using Stripe;
using Stripe.Checkout;

namespace AssetBlock.Infrastructure.Services;

internal sealed class StripePaymentService(
    IOptions<StripeOptions> options,
    ResiliencePipelineProvider<string> resilience,
    ILogger<StripePaymentService> logger) : IPaymentService
{
    private readonly StripeClient _stripeClient = new(options.Value.SecretKey);

    public async Task<StripeCheckoutSession> CreateCheckoutSession(CheckoutLineItem item, Guid userId, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var resolvedSuccessUrl = opts.DefaultSuccessUrl;
        var resolvedCancelUrl = opts.DefaultCancelUrl;

        if (string.IsNullOrWhiteSpace(resolvedSuccessUrl) || string.IsNullOrWhiteSpace(resolvedCancelUrl))
        {
            throw new InvalidOperationException("Stripe SuccessUrl and CancelUrl must be configured.");
        }

        var sessionService = new SessionService(_stripeClient);
        var sessionOptions = new SessionCreateOptions
        {
            Mode = StripeConstants.MODE_PAYMENT,
            SuccessUrl = resolvedSuccessUrl,
            CancelUrl = resolvedCancelUrl,
            Metadata = new Dictionary<string, string>
            {
                { StripeConstants.MetadataKeys.USER_ID, userId.ToString() },
                { StripeConstants.MetadataKeys.ASSET_ID, item.AssetId.ToString() },
                { StripeConstants.MetadataKeys.ASSET_VERSION_ID, item.AssetVersionId.ToString() },
                { StripeConstants.MetadataKeys.CHECKOUT_INTENT_ID, item.CheckoutIntentId.ToString() }
            },
            LineItems =
            [
                new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = item.Currency,
                        UnitAmount = (long)Math.Round(item.UnitAmount * 100, MidpointRounding.AwayFromZero),
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = item.Title
                        }
                    },
                    Quantity = 1
                }
            ]
        };

        var pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STRIPE);
        var session = await pipeline.ExecuteAsync(
            async ct => await sessionService.CreateAsync(sessionOptions, cancellationToken: ct),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.Url))
        {
            throw new InvalidOperationException("Stripe did not return a checkout session id and URL.");
        }

        return new StripeCheckoutSession(session.Id, session.Url);
    }

    public Task<StripeCheckoutCompleted?> VerifyCheckoutCompleted(string payload, string signature, CancellationToken cancellationToken = default)
    {
        var webhookSecret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(webhookSecret))
        {
            throw new InvalidOperationException("Stripe webhook secret is not configured.");
        }

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signature, webhookSecret);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stripe webhook signature validation failed");
            throw new StripeWebhookInvalidSignatureException();
        }

        if (stripeEvent.Type != StripeConstants.Events.CHECKOUT_SESSION_COMPLETED)
        {
            return Task.FromResult<StripeCheckoutCompleted?>(null);
        }

        var session = stripeEvent.Data.Object as Session;
        if (session?.Metadata is null || session.Metadata.Count == 0)
        {
            return Task.FromResult<StripeCheckoutCompleted?>(null);
        }

        if (session.PaymentStatus != StripeConstants.PAYMENT_STATUS_PAID)
        {
            return Task.FromResult<StripeCheckoutCompleted?>(null);
        }

        if (!session.Metadata.TryGetValue(StripeConstants.MetadataKeys.USER_ID, out var userIdStr) ||
            !session.Metadata.TryGetValue(StripeConstants.MetadataKeys.ASSET_ID, out var assetIdStr) ||
            !session.Metadata.TryGetValue(StripeConstants.MetadataKeys.ASSET_VERSION_ID, out var assetVersionIdStr) ||
            !session.Metadata.TryGetValue(StripeConstants.MetadataKeys.CHECKOUT_INTENT_ID, out var checkoutIntentIdStr) ||
            !Guid.TryParse(userIdStr, out var userId) ||
            !Guid.TryParse(assetIdStr, out var assetId) ||
            !Guid.TryParse(assetVersionIdStr, out var assetVersionId) ||
            !Guid.TryParse(checkoutIntentIdStr, out var checkoutIntentId))
        {
            return Task.FromResult<StripeCheckoutCompleted?>(null);
        }

        if (session.AmountTotal is not { } amountTotalInCents || amountTotalInCents <= 0
            || !string.Equals(session.Currency, StripeConstants.CURRENCY_USD, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Paid Stripe checkout session has an invalid amount or currency.");
        }

        var amountTotal = amountTotalInCents / 100m;
        var currency = StripeConstants.CURRENCY_USD;

        return Task.FromResult<StripeCheckoutCompleted?>(
            new StripeCheckoutCompleted(checkoutIntentId, userId, assetId, assetVersionId, session.Id, amountTotal, currency));
    }
}
