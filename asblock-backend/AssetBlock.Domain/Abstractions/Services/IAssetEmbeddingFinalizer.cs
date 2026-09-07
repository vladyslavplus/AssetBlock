namespace AssetBlock.Domain.Abstractions.Services;

public sealed record FinalizeEmbeddingParameters(
    Guid JobId,
    Guid LeaseToken,
    Guid AssetId,
    Guid AssetVersionId,
    long SourceRevision,
    string ContentHash,
    string ModelKey,
    string Provider,
    string ModelId,
    string ModelRevision,
    string ModelDigest,
    int Dimension,
    string ContentSchemaVersion,
    float[] Vector);

public enum EmbeddingFinalizationStatus
{
    COMMITTED,
    NO_OP_STALE_OR_SUPERCEDED,
    LEASE_LOST
}

public interface IAssetEmbeddingFinalizer
{
    Task<EmbeddingFinalizationStatus> Finalize(
        FinalizeEmbeddingParameters parameters,
        CancellationToken cancellationToken = default);

    Task<bool> MarkJobNoOp(
        Guid jobId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);
}
