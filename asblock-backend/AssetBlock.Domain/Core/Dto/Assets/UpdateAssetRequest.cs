namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record UpdateAssetRequest(
    string? Title = null,
    string? Description = null,
    decimal? Price = null,
    Guid? CategoryId = null);
