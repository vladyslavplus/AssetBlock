namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when collection item position uniqueness conflicts under concurrent mutation.</summary>
public sealed class CollectionItemConcurrencyException()
    : Exception("Collection item positions changed concurrently.");
