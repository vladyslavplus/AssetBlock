namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>Internal projection for checkout / download resolution.</summary>
public sealed record AssetCurrentVersionSnapshot(
    Guid AssetId,
    Guid AssetVersionId,
    Guid AuthorId,
    string Title,
    string? Description,
    decimal Price,
    DateTimeOffset? DeletedAt,
    int VersionNumber,
    string FileName,
    string StorageKey,
    long ContentLength,
    string ContentSha256);
