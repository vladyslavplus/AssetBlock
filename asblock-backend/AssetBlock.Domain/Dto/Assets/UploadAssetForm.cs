namespace AssetBlock.Domain.Dto.Assets;

/// <summary>
/// Form DTO for asset upload (multipart/form-data). File is sent as separate form field "file".
/// </summary>
public sealed class UploadAssetForm
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public Guid CategoryId { get; set; }
}
