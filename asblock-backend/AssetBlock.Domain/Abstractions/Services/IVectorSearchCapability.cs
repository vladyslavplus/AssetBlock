namespace AssetBlock.Domain.Abstractions.Services;

public sealed record VectorSearchCapabilityResult(
    bool IsAvailable,
    bool IsConfigEnabled,
    bool HasExtension,
    string? ModelKey = null,
    string? Reason = null)
{
    public static VectorSearchCapabilityResult Disabled() =>
        new(false, false, false, null, "Embeddings are disabled in configuration.");

    public static VectorSearchCapabilityResult ExtensionMissing(string modelKey) =>
        new(false, true, false, modelKey, "PostgreSQL vector extension is not installed.");

    public static VectorSearchCapabilityResult Available(string modelKey) =>
        new(true, true, true, modelKey, null);
}

public interface IVectorSearchCapability
{
    Task<VectorSearchCapabilityResult> CheckCapability(CancellationToken cancellationToken = default);
    Task<bool> IsVectorSearchAvailable(CancellationToken cancellationToken = default);
}
