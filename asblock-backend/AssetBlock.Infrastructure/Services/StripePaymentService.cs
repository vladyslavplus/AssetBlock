using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Payments;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Polly;
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

    public async Task<StripeCheckoutSession> CreateCheckoutSession(
        CheckoutSessionDraft draft,
        CancellationToken cancellationToken = default)
    {
        StripeOptions opts = options.Value;
        var resolvedSuccessUrl = opts.SuccessUrl;
        var resolvedCancelUrl = opts.CancelUrl;

        if (string.IsNullOrWhiteSpace(resolvedSuccessUrl) || string.IsNullOrWhiteSpace(resolvedCancelUrl))
        {
            throw new InvalidOperationException("Stripe SuccessUrl and CancelUrl must be configured.");
        }

        if (draft.Lines.Count == 0)
        {
            throw new InvalidOperationException("Checkout session draft must contain at least one line.");
        }

        if (!IsoCurrency.TryNormalize(draft.Currency, out var currency) || currency != draft.Currency)
        {
            throw new InvalidOperationException("Checkout draft currency must be a lowercase ISO 4217 code.");
        }

        var lineItems = new List<SessionLineItemOptions>(draft.Lines.Count);
        long totalCents = 0;
        foreach (CheckoutSessionDraftLine line in draft.Lines)
        {
            if (!string.Equals(line.Currency, currency, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Checkout draft line currency must match header currency.");
            }

            var usdAmount = UsdAmount.FromDollarsExact(line.Amount);
            if (usdAmount.Cents is <= 0 or > BundlePriceAllocator.MAX_AMOUNT_CENTS)
            {
                throw new InvalidOperationException(
                    $"Checkout draft line amount must be between 1 and {BundlePriceAllocator.MAX_AMOUNT_CENTS} cents.");
            }

            var cents = usdAmount.Cents;
            totalCents = checked(totalCents + cents);
            lineItems.Add(new SessionLineItemOptions
            {
                PriceData = new SessionLineItemPriceDataOptions
                {
                    Currency = currency,
                    UnitAmount = cents,
                    ProductData = new SessionLineItemPriceDataProductDataOptions
                    {
                        Name = line.Title
                    }
                },
                Quantity = 1
            });
        }

        if (totalCents is <= 0 or > BundlePriceAllocator.MAX_AMOUNT_CENTS)
        {
            throw new InvalidOperationException(
                $"Checkout session total must be between 1 and {BundlePriceAllocator.MAX_AMOUNT_CENTS} cents.");
        }

        var sessionService = new SessionService(_stripeClient);
        var sessionOptions = new SessionCreateOptions
        {
            Mode = StripeConstants.MODE_PAYMENT,
            SuccessUrl = resolvedSuccessUrl,
            CancelUrl = resolvedCancelUrl,
            ExpiresAt = draft.ExpiresAt.UtcDateTime,
            Metadata = new Dictionary<string, string>
            {
                { StripeConstants.MetadataKeys.USER_ID, draft.UserId.ToString() },
                { StripeConstants.MetadataKeys.CHECKOUT_INTENT_ID, draft.CheckoutIntentId.ToString() }
            },
            LineItems = lineItems
        };

        ResiliencePipeline pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STRIPE);
        var requestOptions = new RequestOptions
        {
            IdempotencyKey = draft.CheckoutIntentId.ToString("N")
        };
        Session session = await pipeline.ExecuteAsync(
            async ct => await sessionService.CreateAsync(sessionOptions, requestOptions, ct),
            cancellationToken);
        if (string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.Url))
        {
            throw new InvalidOperationException("Stripe did not return a checkout session id and URL.");
        }

        return new StripeCheckoutSession(session.Id, session.Url);
    }

    public async Task<StripeCheckoutSessionSnapshot> GetCheckoutSession(
        string stripeSessionId,
        CancellationToken cancellationToken = default)
    {
        var sessionService = new SessionService(_stripeClient);
        ResiliencePipeline pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STRIPE);
        Session session = await pipeline.ExecuteAsync(
            async ct => await sessionService.GetAsync(stripeSessionId, cancellationToken: ct),
            cancellationToken);

        if (string.IsNullOrWhiteSpace(session.Id) || string.IsNullOrWhiteSpace(session.Status))
        {
            throw new InvalidOperationException("Stripe returned an invalid checkout session.");
        }

        return new StripeCheckoutSessionSnapshot(
            session.Id,
            session.Status,
            session.Url,
            MapPaidCheckout(session));
    }

    public Task<StripeCheckoutCompleted?> VerifyCheckoutCompleted(
        string payload,
        string signature,
        CancellationToken cancellationToken = default)
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

        return Task.FromResult(stripeEvent.Data.Object is Session session ? MapPaidCheckout(session) : null);
    }

    private static StripeCheckoutCompleted? MapPaidCheckout(Session session)
    {
        if (session.Metadata is null
            || session.Metadata.Count == 0
            || string.IsNullOrWhiteSpace(session.Id))
        {
            return null;
        }

        if (session.PaymentStatus != StripeConstants.PAYMENT_STATUS_PAID)
        {
            return null;
        }

        if (!session.Metadata.TryGetValue(StripeConstants.MetadataKeys.USER_ID, out var userIdStr)
            || !session.Metadata.TryGetValue(StripeConstants.MetadataKeys.CHECKOUT_INTENT_ID, out var checkoutIntentIdStr)
            || !Guid.TryParse(userIdStr, out Guid userId)
            || !Guid.TryParse(checkoutIntentIdStr, out Guid checkoutIntentId))
        {
            return null;
        }

        if (session.AmountTotal is not { } amountTotalInCents || amountTotalInCents <= 0
            || !IsoCurrency.TryNormalize(session.Currency, out var currency)
            || currency != StripeConstants.CURRENCY_USD)
        {
            throw new InvalidOperationException("Paid Stripe checkout session has an invalid amount or currency.");
        }

        var amountTotal = UsdAmount.FromCents(amountTotalInCents).Dollars;
        return new StripeCheckoutCompleted(checkoutIntentId, userId, session.Id, amountTotal, currency);
    }
}
