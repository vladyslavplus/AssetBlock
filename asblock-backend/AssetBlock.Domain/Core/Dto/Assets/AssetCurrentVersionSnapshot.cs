namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>Internal projection for checkout / download / detail resolution.</summary>
public sealed record AssetCurrentVersionSnapshot(
    Guid AssetId,
    Guid AssetVersionId,
    Guid AuthorId,
    string Title,
    string? Description,
    decimal Price,
    DateTimeOffset? DeletedAt,
    int VersionNumber,
    DateTimeOffset VersionCreatedAt,
    string FileName,
    string StorageKey,
    long ContentLength,
    string ContentSha256,
    string LicenseCode,
    string LicenseTemplateVersion,
    string LicenseDisplayName,
    string LicenseTerms);
