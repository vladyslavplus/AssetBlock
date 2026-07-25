using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Immutable paid line copied from a checkout intent item. Historical price/license live here.
/// </summary>
public class OrderLine
{
    public required Guid Id { get; init; }
    public required Guid OrderId { get; set; }
    public required Guid AssetId { get; set; }
    public required Guid AssetVersionId { get; set; }
    public required Guid SellerId { get; set; }
    public required int Position { get; set; }
    public required string AssetTitleSnapshot { get; set; }
    public required int VersionNumber { get; set; }
    public required decimal ListPrice { get; set; }
    public required decimal PricePaid { get; set; }

    public required AssetLicenseCode LicenseCode { get; set; }
    public required string LicenseTemplateVersion { get; set; }
    public required string LicenseDisplayName { get; set; }
    public required string LicenseTerms { get; set; }

    public Order Order { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
    public AssetVersion AssetVersion { get; set; } = null!;
    public User Seller { get; set; } = null!;
    public Purchase? Purchase { get; set; }
}
