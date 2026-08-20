using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Payments;
using Ardalis.Result;

namespace AssetBlock.Application.UseCases.Bundles;

/// <summary>
/// Locks assets, validates seller ownership/availability/pricing, and builds revision item drafts.
/// Call inside an open unit-of-work transaction so row locks hold until commit.
/// </summary>
internal static class BundleRevisionDraftBuilder
{
    public static async Task<Result<(decimal ListPriceTotal, IReadOnlyList<BundleRevisionItemDraft> Items)>> Build(
        IBundleStore bundleStore,
        IAssetStore assetStore,
        Guid sellerId,
        IReadOnlyList<Guid> assetIds,
        decimal bundlePrice,
        CancellationToken cancellationToken)
    {
        var sortedIds = assetIds.OrderBy(id => id).ToArray();
        await bundleStore.LockAssetsInOrder(sortedIds, cancellationToken);

        var items = new List<BundleRevisionItemDraft>(assetIds.Count);
        decimal listPriceTotal = 0m;

        for (var i = 0; i < assetIds.Count; i++)
        {
            var assetId = assetIds[i];
            var snapshot = await assetStore.GetCurrentVersionSnapshot(assetId, cancellationToken);
            if (snapshot is null
                || snapshot.DeletedAt.HasValue
                || snapshot.AuthorId != sellerId
                || snapshot.Price <= 0)
            {
                return ResultError.Error<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>(
                    ErrorCodes.ERR_BUNDLE_ASSET_INVALID);
            }

            try
            {
                _ = BundlePriceAllocator.ToCents(snapshot.Price);
            }
            catch (ArgumentException)
            {
                return ResultError.Error<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>(
                    ErrorCodes.ERR_BUNDLE_ASSET_INVALID);
            }

            listPriceTotal += snapshot.Price;
            items.Add(new BundleRevisionItemDraft(
                snapshot.AssetId,
                Position: i + 1,
                snapshot.Title,
                snapshot.Price));
        }

        if (bundlePrice <= 0 || bundlePrice >= listPriceTotal)
        {
            return ResultError.Error<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>(
                ErrorCodes.ERR_BUNDLE_PRICE_INVALID);
        }

        long bundleTotalCents;
        try
        {
            bundleTotalCents = BundlePriceAllocator.ToCents(bundlePrice);
        }
        catch (ArgumentException)
        {
            return ResultError.Error<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>(
                ErrorCodes.ERR_BUNDLE_PRICE_INVALID);
        }

        if (bundleTotalCents < items.Count)
        {
            return ResultError.Error<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>(
                ErrorCodes.ERR_BUNDLE_PRICE_INVALID);
        }

        return Result.Success<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>((listPriceTotal, items));
    }
}
