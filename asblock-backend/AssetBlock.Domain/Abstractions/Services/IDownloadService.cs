using AssetBlock.Domain.Core.Primitives.Api;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Authorizes asset downloads and streams decrypted content to a destination.
/// </summary>
public interface IDownloadService
{
    /// <summary>
    /// Authorizes a download for the user. When <paramref name="versionId"/> is null the latest entitled version is resolved.
    /// Authors may download any version; purchasers may download the purchased version and any higher version number.
    /// </summary>
    Task<DownloadAuthorization> AuthorizeDownload(Guid assetId, Guid userId, Guid? versionId = null, CancellationToken cancellationToken = default);
    Task CopyDecrypted(string storageKey, Stream destination, CancellationToken cancellationToken = default);
}
