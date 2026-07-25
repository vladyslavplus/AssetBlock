using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Exceptions;

namespace AssetBlock.WebApi.IntegrationTests.Support.Fakes;

/// <summary>Deterministic Stripe stand-in for HTTP checkout integration tests (Postgres stays real).</summary>
public sealed class FakePaymentService : IPaymentService
{
    public Task<StripeCheckoutSession> CreateCheckoutSession(
        CheckoutSessionDraft draft,
        CancellationToken cancellationToken = default)
    {
        var id = $"cs_fake_{draft.CheckoutIntentId:N}";
        return Task.FromResult(new StripeCheckoutSession(id, $"https://checkout.test/{id}"));
    }

    public Task<StripeCheckoutSessionSnapshot> GetCheckoutSession(
        string stripeSessionId,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(new StripeCheckoutSessionSnapshot(
            stripeSessionId,
            "open",
            $"https://checkout.test/{stripeSessionId}"));
    }

    public Task<StripeCheckoutCompleted?> VerifyCheckoutCompleted(
        string payload,
        string signature,
        CancellationToken cancellationToken = default)
    {
        // Mirror StripePaymentService: missing/invalid signatures are rejected before event parsing.
        if (string.IsNullOrWhiteSpace(signature)
            || string.Equals(signature, "bad", StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(payload))
        {
            throw new StripeWebhookInvalidSignatureException();
        }

        return Task.FromResult<StripeCheckoutCompleted?>(null);
    }
}
