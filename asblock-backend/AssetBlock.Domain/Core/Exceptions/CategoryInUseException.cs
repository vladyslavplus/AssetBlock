namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when category deletion fails due to being referenced by an asset.</summary>
public sealed class CategoryInUseException() : Exception("The category cannot be deleted because it is in use by one or more assets.");
