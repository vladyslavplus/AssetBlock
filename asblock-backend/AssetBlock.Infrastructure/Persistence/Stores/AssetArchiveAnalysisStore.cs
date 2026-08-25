using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class AssetArchiveAnalysisStore(ApplicationDbContext dbContext) : IAssetArchiveAnalysisStore
{
    public Task<AssetArchiveAnalysis?> GetByVersionId(Guid assetVersionId, CancellationToken cancellationToken = default)
    {
        return dbContext.AssetArchiveAnalyses
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.AssetVersionId == assetVersionId, cancellationToken);
    }
}
