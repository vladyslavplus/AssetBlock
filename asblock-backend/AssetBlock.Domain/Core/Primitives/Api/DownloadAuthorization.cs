namespace AssetBlock.Domain.Core.Primitives.Api;

/// <summary>Result of authorizing an asset download (no content stream).</summary>
public sealed record DownloadAuthorization(AssetDownloadStatus Status, DownloadPermit? Permit = null);

/// <summary>Permit to stream a decrypted asset to the caller.</summary>
public sealed record DownloadPermit(string StorageKey, string FileName);

public enum AssetDownloadStatus
{
    SUCCESS,
    NOT_FOUND,
    FORBIDDEN,
    RATE_LIMITED
}
