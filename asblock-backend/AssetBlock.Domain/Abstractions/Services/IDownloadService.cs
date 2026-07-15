using AssetBlock.Domain.Core.Primitives.Api;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Authorizes asset downloads and streams decrypted content to a destination.
/// </summary>
public interface IDownloadService
{
    Task<DownloadAuthorization> AuthorizeDownload(Guid assetId, Guid userId, CancellationToken cancellationToken = default);
    Task CopyDecrypted(string storageKey, Stream destination, CancellationToken cancellationToken = default);
}
