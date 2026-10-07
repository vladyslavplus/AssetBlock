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

    /// <summary>Optional owned pre-upload draft workspace the bytes bind to.</summary>
    public Guid? WorkspaceId { get; set; }

    /// <summary>Required with WorkspaceId: the workspace revision the client saw before streaming.</summary>
    public long? ExpectedWorkspaceRevision { get; set; }

    /// <summary>New version file. Form field name: "file".</summary>
    [FromForm(Name = "file")]
    [Required]
    public IFormFile File { get; set; } = null!;
}
