using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stripe;
using Stripe.Checkout;

namespace AssetBlock.Infrastructure.Services;

internal sealed class StripePaymentService(
    IOptions<StripeOptions> options,
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    ILogger<StripePaymentService> logger) : IPaymentService
{
    public async Task<string> CreateCheckoutSession(Guid assetId, Guid userId, string successUrl, string cancelUrl, CancellationToken cancellationToken = default)
    {
        var asset = await assetStore.GetById(assetId, cancellationToken)
            ?? throw new InvalidOperationException("Asset not found.");
        StripeConfiguration.ApiKey = options.Value.SecretKey;

        var sessionService = new SessionService();
        var sessionOptions = new SessionCreateOptions
        {
            Mode = "payment",
            SuccessUrl = successUrl,
            CancelUrl = cancelUrl,
            Metadata = new Dictionary<string, string>
            {
                { "userId", userId.ToString() },
                { "assetId", assetId.ToString() }
            },
            LineItems =
            [
                new SessionLineItemOptions
                {
                    PriceData = new SessionLineItemPriceDataOptions
                    {
                        Currency = "usd",
                        UnitAmount = (long)Math.Round(asset.Price * 100),
                        ProductData = new SessionLineItemPriceDataProductDataOptions
                        {
                            Name = asset.Title, Description = asset.Description ?? string.Empty
                        }
                    },
                    Quantity = 1
                }
            ]
        };

        var session = await sessionService.CreateAsync(sessionOptions, cancellationToken: cancellationToken);
        return session.Url ?? throw new InvalidOperationException("Stripe did not return a session URL.");
    }

    public async Task<(Guid UserId, Guid AssetId)?> HandleCheckoutCompleted(string payload, string signature, CancellationToken cancellationToken = default)
    {
        var webhookSecret = options.Value.WebhookSecret;
        if (string.IsNullOrEmpty(webhookSecret))
        {
            return null;
        }

        Event stripeEvent;
        try
        {
            stripeEvent = EventUtility.ConstructEvent(payload, signature, webhookSecret);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Stripe webhook signature validation failed");
            return null;
        }

        if (stripeEvent.Type != "checkout.session.completed")
        {
            return null;
        }

        var session = stripeEvent.Data.Object as Session;
        if (session?.Metadata is null || session.Metadata.Count == 0)
        {
            return null;
        }

        if (session.PaymentStatus != "paid")
        {
            return null;
        }

        if (!session.Metadata.TryGetValue("userId", out var userIdStr) ||
            !session.Metadata.TryGetValue("assetId", out var assetIdStr) ||
            !Guid.TryParse(userIdStr, out var userId) ||
            !Guid.TryParse(assetIdStr, out var assetId))
        {
            return null;
        }

        var existing = await purchaseStore.GetByStripePaymentId(session.Id, cancellationToken);
        if (existing is not null)
        {
            return (userId, assetId);
        }

        var purchase = new Purchase
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AssetId = assetId,
            StripePaymentId = session.Id,
            PurchasedAt = DateTime.UtcNow
        };
        await purchaseStore.Add(purchase, cancellationToken);
        return (userId, assetId);
    }
}
