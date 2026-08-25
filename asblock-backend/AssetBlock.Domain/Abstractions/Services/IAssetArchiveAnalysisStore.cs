using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAssetArchiveAnalysisStore
{
    Task<AssetArchiveAnalysis?> GetByVersionId(Guid assetVersionId, CancellationToken cancellationToken = default);
}
