namespace AssetBlock.Domain.Core.Primitives.Api;

/// <summary>Result of requesting an asset download. Caller disposes Content when Status is Success.</summary>
public sealed record AssetDownloadResult(AssetDownloadStatus Status, Stream? Content, string? FileName);

public enum AssetDownloadStatus
{
    Success,
    NotFound,
    Forbidden,
    RateLimited
}
