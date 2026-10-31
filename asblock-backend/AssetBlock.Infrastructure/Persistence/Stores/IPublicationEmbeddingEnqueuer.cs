namespace AssetBlock.Infrastructure.Persistence.Stores;

public interface IPublicationEmbeddingEnqueuer
{
    Task EnqueueApprovedPublicationEmbedding(Guid assetId, CancellationToken cancellationToken = default);
}
