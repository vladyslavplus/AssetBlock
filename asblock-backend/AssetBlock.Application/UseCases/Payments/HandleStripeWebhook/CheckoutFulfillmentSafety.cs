using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;

internal static class CheckoutFulfillmentSafety
{
    public sealed record FulfillmentSafetyResult(bool IsSafe, string? BlockReason);

    public static async Task<FulfillmentSafetyResult> ValidateItems(
        IAssetStore assetStore,
        IReadOnlyList<CheckoutIntentItem> items,
        CancellationToken cancellationToken)
    {
        foreach (CheckoutIntentItem item in items)
        {
            if (!item.PublicationSnapshotId.HasValue)
            {
                return new FulfillmentSafetyResult(false, "missing_publication_snapshot");
            }

            AssetVersion? version = await assetStore.GetVersion(item.AssetId, item.AssetVersionId, cancellationToken);
            if (version is null)
            {
                return new FulfillmentSafetyResult(false, "missing_asset_version");
            }

            if (version.ProcessingStatus is AssetVersionProcessingStatus.REJECTED
                or AssetVersionProcessingStatus.PROCESSING_FAILED)
            {
                return new FulfillmentSafetyResult(false, "unsafe_processing_status");
            }

            if (version.ProcessingStatus != AssetVersionProcessingStatus.READY)
            {
                return new FulfillmentSafetyResult(false, "version_not_ready");
            }

            var offeringValid = await assetStore.IsPinnedSaleOfferingValid(
                item.AssetId,
                item.AssetVersionId,
                item.PublicationSnapshotId.Value,
                cancellationToken);
            if (!offeringValid)
            {
                return new FulfillmentSafetyResult(false, "offering_no_longer_valid");
            }
        }

        return new FulfillmentSafetyResult(true, null);
    }

    public static string SerializeSafePaymentFacts(StripeCheckoutCompleted verified) =>
        JsonSerializer.Serialize(new
        {
            verified.CheckoutIntentId,
            verified.StripeSessionId,
            verified.UserId,
            verified.AmountTotal,
            verified.Currency
        });

    public static string SerializeItemIdentities(IReadOnlyList<CheckoutIntentItem> items) =>
        JsonSerializer.Serialize(items
            .OrderBy(i => i.Position)
            .Select(i => new
            {
                i.AssetId,
                i.AssetVersionId,
                i.PublicationSnapshotId,
                i.VersionNumber,
                i.LicenseCode,
                i.LicenseTemplateVersion,
                i.AllocatedPrice
            }));
}
