namespace AssetBlock.Domain.Dto.Payments;

public sealed record CreateCheckoutRequest(Guid AssetId, string SuccessUrl, string CancelUrl);
