namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when a purchase entitlement unique constraint is violated without a durable order match.</summary>
public sealed class DuplicateEntitlementException() : Exception("Purchase entitlement already exists.");
