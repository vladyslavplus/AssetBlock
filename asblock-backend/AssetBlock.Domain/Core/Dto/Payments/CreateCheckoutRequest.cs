namespace AssetBlock.Domain.Core.Dto.Payments;

/// <summary>
/// Analytics fields are optional. The public browser client does not send visitor/session ids; the BFF
/// attaches them, and a direct API caller may omit them entirely.
/// </summary>
public sealed record CreateCheckoutRequest(
    Guid AssetId,
    CheckoutAttributionRequest? Attribution = null,
    Guid? AnalyticsVisitorId = null,
    Guid? AnalyticsSessionId = null);
