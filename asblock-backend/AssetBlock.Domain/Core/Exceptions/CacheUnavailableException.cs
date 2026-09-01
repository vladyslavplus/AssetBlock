namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Raised when a configured cache cannot safely distinguish a read from a cache miss.</summary>
public sealed class CacheUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException);
