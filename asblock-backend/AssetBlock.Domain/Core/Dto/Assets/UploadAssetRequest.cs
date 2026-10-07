namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record UploadAssetRequest(
    string Title,
    string? Description,
    decimal Price,
    Guid CategoryId,
    string LicenseCode,
    int? DownloadLimitPerHour = null,
    List<string>? Tags = null,
    Guid? WorkspaceId = null,
    long? ExpectedWorkspaceRevision = null);
