using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>Private seller workspace scoped to an asset and optionally a specific version.</summary>
public class AssetDraftWorkspace : BaseEntity
{
    public required Guid AssetId { get; init; }

    /// <summary>Null for the single pre-upload workspace per asset.</summary>
    public Guid? AssetVersionId { get; init; }

    /// <summary>Non-null scope key: pre-upload sentinel or the bound version id.</summary>
    public required Guid WorkspaceVersionScopeKey { get; init; }

    public required Guid ScopeId { get; init; }

    public long WorkspaceRevision { get; set; } = 1;

    public int MaterialMetadataHeadRevision { get; set; }
    public int SourceDeclarationHeadRevision { get; set; }
    public int SellerEvidenceHeadRevision { get; set; }

    public long CaseRevision { get; set; } = 1;

    public Asset Asset { get; set; } = null!;
    public AssetVersion? AssetVersion { get; set; }
}
