namespace AssetBlock.Application.UseCases.Payments.GetCheckoutStatus;

public sealed record GetCheckoutStatusResponse(
    string Status,
    Guid CheckoutIntentId,
    Guid? OrderId,
    string ProductTitle,
    Guid? AssetId,
    Guid? BundleId);
