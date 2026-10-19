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

    /// <summary>Immutable S3 object generation returned by the provider when uploaded. Null means cleanup must fail closed.</summary>
    public string? StorageVersionId { get; set; }

    /// <summary>When unprotected abandoned-upload payload was retired while immutable proof rows were retained.</summary>
    public DateTimeOffset? StorageRetiredAt { get; set; }

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

    public AssetVersionProcessingStatus ProcessingStatus { get; set; } = AssetVersionProcessingStatus.PENDING_INSPECTION;
    public string? ProcessingErrorCode { get; set; }
    public string? ProcessingErrorSummary { get; set; }
    public required DateTimeOffset ProcessingUpdatedAt { get; set; }

    /// <summary>
    /// Exact-version safety restriction from an actual unsafe, infected, or integrity failure.
    /// Denies byte delivery and reference use for this version without touching entitlement or proof.
    /// </summary>
    public AssetVersionSafetyRestriction SafetyRestriction { get; set; } = AssetVersionSafetyRestriction.NONE;

    public string? SafetyRestrictionReason { get; set; }

    public DateTimeOffset? SafetyRestrictedAt { get; set; }

    /// <summary>Report currently selected for this version; null when no binding is valid.</summary>
    public Guid? SelectedCodeAnalysisReportId { get; set; }

    /// <summary>
    /// Monotonic generation of this version's analysis selection binding. Bumped when material or
    /// declaration input changes; price-only changes never bump it.
    /// </summary>
    public int AnalysisSelectionGeneration { get; set; } = 1;

    public Asset Asset { get; set; } = null!;
    public AssetArchiveAnalysis? ArchiveAnalysis { get; set; }
    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();
}
