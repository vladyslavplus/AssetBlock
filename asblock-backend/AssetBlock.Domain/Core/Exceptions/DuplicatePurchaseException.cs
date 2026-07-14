namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when a purchase violates UserId+AssetId or StripePaymentId uniqueness.</summary>
public sealed class DuplicatePurchaseException() : Exception("Purchase already exists.");
