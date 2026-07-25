using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.Payments.Checkout;

internal sealed record CheckoutDraft(
    Guid UserId,
    Guid? AssetId,
    Guid? BundleId,
    Guid? BundleRevisionId,
    string ProductTitle,
    decimal AmountTotal,
    string Currency,
    IReadOnlyList<CheckoutDraftItem> Items);

internal sealed record CheckoutDraftItem(
    Guid AssetId,
    Guid AssetVersionId,
    Guid SellerId,
    int Position,
    string AssetTitleSnapshot,
    int VersionNumber,
    decimal ListPrice,
    decimal AllocatedPrice,
    AssetLicenseCode LicenseCode,
    string LicenseTemplateVersion,
    string LicenseDisplayName,
    string LicenseTerms);
