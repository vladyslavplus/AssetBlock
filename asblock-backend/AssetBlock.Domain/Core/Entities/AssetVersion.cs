using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Immutable published content version of an asset. Metadata, blob key, hash, and license
/// snapshot must not change after insert.
/// </summary>
public class AssetVersion : BaseEntity
{
    public required Guid AssetId { get; set; }
    public required int VersionNumber { get; set; }
    public required bool IsCurrent { get; set; }

    /// <summary>Object key in MinIO. Immutable after insert.</summary>
    public required string StorageKey { get; set; }

    /// <summary>Original display file name. Immutable after insert.</summary>
    public required string FileName { get; set; }

    /// <summary>Plaintext byte count before encryption.</summary>
    public required long ContentLength { get; set; }

    /// <summary>Lowercase 64-character hex SHA-256 of plaintext.</summary>
    public required string ContentSha256 { get; set; }

    /// <summary>Required release notes snapshot for this version.</summary>
    public required string ReleaseNotes { get; set; }

    public required AssetLicenseCode LicenseCode { get; set; }
    public required string LicenseTemplateVersion { get; set; }
    public required string LicenseDisplayName { get; set; }
    public required string LicenseTerms { get; set; }

    public Asset Asset { get; set; } = null!;
    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();
}
