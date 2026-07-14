namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when the catalog search dependency is unavailable.</summary>
public sealed class SearchUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);
