using AssetBlock.Domain.Core.Primitives.Api;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Provides a decrypted asset stream for download when the user has access (author or purchaser).
/// </summary>
public interface IDownloadService
{
    Task<AssetDownloadResult> GetAssetStream(Guid assetId, Guid userId, CancellationToken cancellationToken = default);
}
