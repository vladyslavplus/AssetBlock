namespace AssetBlock.Domain.Dto.Assets;

public sealed record UploadAssetRequest(
    string Title,
    string? Description,
    decimal Price,
    Guid CategoryId);
