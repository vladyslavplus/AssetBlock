namespace AssetBlock.Domain.Abstractions.Services;

public interface IEmbeddingBackfillCoordinator
{
    Task<int> RunBackfillCycle(CancellationToken cancellationToken = default);
}
