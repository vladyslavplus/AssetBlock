namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Provides a decrypted asset stream for download when the user has purchased the asset.
/// </summary>
public interface IDownloadService
{
    /// <summary>Returns (stream, fileName) if the user has access; null otherwise. Caller disposes stream.</summary>
    Task<(Stream Content, string FileName)?> GetAssetStream(Guid assetId, Guid userId, CancellationToken cancellationToken = default);
}
