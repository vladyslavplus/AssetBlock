namespace AssetBlock.Domain.Core.Dto.Payments;

/// <summary>
/// Server-derived Stripe checkout session draft. Never accept amounts, titles, or product ids from the browser.
/// </summary>
public sealed record CheckoutSessionDraft(
    Guid CheckoutIntentId,
    Guid UserId,
    DateTimeOffset ExpiresAt,
    string Currency,
    IReadOnlyList<CheckoutSessionDraftLine> Lines);

/// <summary>One Stripe line corresponding to a checkout intent item.</summary>
public sealed record CheckoutSessionDraftLine(
    string Title,
    decimal Amount,
    string Currency);

/// <summary>Verified Stripe checkout.session.completed data (no DB writes).</summary>
public sealed record StripeCheckoutCompleted(
    Guid CheckoutIntentId,
    Guid UserId,
    string StripeSessionId,
    decimal AmountTotal,
    string Currency,
    string StripeEventId = "");
