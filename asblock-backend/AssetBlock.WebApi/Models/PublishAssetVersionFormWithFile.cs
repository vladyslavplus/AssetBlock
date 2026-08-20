using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Models;

/// <summary>
/// API model for publishing a new asset version. File part name: "file".
/// </summary>
public sealed class PublishAssetVersionFormWithFile
{
    /// <summary>License code identifying which platform license template applies (e.g. PERSONAL, COMMERCIAL).</summary>
    public string LicenseCode { get; set; } = string.Empty;

    /// <summary>Release notes describing what changed in this version.</summary>
    [Required]
    public string ReleaseNotes { get; set; } = string.Empty;

    /// <summary>New version file. Form field name: "file".</summary>
    [FromForm(Name = "file")]
    [Required]
    public IFormFile File { get; set; } = null!;
}
