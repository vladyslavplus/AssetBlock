namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when an order unique constraint is violated (Stripe session or checkout intent).</summary>
public sealed class DuplicateOrderException() : Exception("Order already exists.");
