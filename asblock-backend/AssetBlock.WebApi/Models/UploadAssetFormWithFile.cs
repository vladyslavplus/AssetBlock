using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Models;

/// <summary>
/// API model for asset upload so Swagger/OpenAPI shows a file input. File part name: "file".
/// </summary>
public sealed class UploadAssetFormWithFile
{
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public Guid CategoryId { get; set; }

    /// <summary>Asset file (any extension allowed). Form field name: "file".</summary>
    [FromForm(Name = "file")]
    public IFormFile? File { get; set; }
}
