using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Immutable checkout line pinned at session creation. Later asset/bundle edits cannot change it.
/// </summary>
public class CheckoutIntentItem
{
    public required Guid Id { get; init; }
    public required Guid CheckoutIntentId { get; set; }
    public required Guid AssetId { get; set; }
    public required Guid AssetVersionId { get; set; }
    public required Guid SellerId { get; set; }
    public required int Position { get; set; }
    public required string AssetTitleSnapshot { get; set; }
    public required int VersionNumber { get; set; }
    public required decimal ListPrice { get; set; }
    public required decimal AllocatedPrice { get; set; }

    public required AssetLicenseCode LicenseCode { get; set; }
    public required string LicenseTemplateVersion { get; set; }
    public required string LicenseDisplayName { get; set; }
    public required string LicenseTerms { get; set; }

    public CheckoutIntent CheckoutIntent { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
    public AssetVersion AssetVersion { get; set; } = null!;
    public User Seller { get; set; } = null!;
}
