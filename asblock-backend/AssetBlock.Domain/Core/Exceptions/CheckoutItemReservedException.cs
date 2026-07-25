namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Raised when a (UserId, AssetId) checkout reservation unique constraint is violated.</summary>
public sealed class CheckoutItemReservedException()
    : Exception("A checkout reservation already exists for this user and asset.");
