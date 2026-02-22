namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record UploadAssetRequest(
    string Title,
    string? Description,
    decimal Price,
    Guid CategoryId,
    int? DownloadLimitPerHour = null,
    List<string>? Tags = null);
