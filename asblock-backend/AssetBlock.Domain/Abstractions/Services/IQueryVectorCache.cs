namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Process-local bounded in-memory cache for query embedding vectors.
/// Not distributed to prevent unbounded remote cache growth.
/// </summary>
public interface IQueryVectorCache
{
    float[]? Get(string modelKey, string searchHash);
    void Set(string modelKey, string searchHash, float[] vector, TimeSpan expiration);
}
