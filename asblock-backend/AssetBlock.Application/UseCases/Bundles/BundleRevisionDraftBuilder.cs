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
        long listPriceTotalCents = 0;

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

            UsdAmount listPrice;
            try
            {
                listPrice = UsdAmount.FromDollarsExact(snapshot.Price);
            }
            catch (ArgumentException)
            {
                return ResultError.Error<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>(
                    ErrorCodes.ERR_BUNDLE_ASSET_INVALID);
            }

            if (listPrice.Cents <= 0 || listPrice.Cents > BundlePriceAllocator.MAX_AMOUNT_CENTS)
            {
                return ResultError.Error<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>(
                    ErrorCodes.ERR_BUNDLE_ASSET_INVALID);
            }

            listPriceTotalCents = checked(listPriceTotalCents + listPrice.Cents);
            items.Add(new BundleRevisionItemDraft(
                snapshot.AssetId,
                Position: i + 1,
                snapshot.Title,
                listPrice.Dollars));
        }

        UsdAmount bundleAmount;
        try
        {
            bundleAmount = UsdAmount.FromDollarsExact(bundlePrice);
        }
        catch (ArgumentException)
        {
            return ResultError.Error<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>(
                ErrorCodes.ERR_BUNDLE_PRICE_INVALID);
        }

        if (bundleAmount.Cents <= 0
            || bundleAmount.Cents > BundlePriceAllocator.MAX_AMOUNT_CENTS
            || bundleAmount.Cents >= listPriceTotalCents
            || bundleAmount.Cents < items.Count)
        {
            return ResultError.Error<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>(
                ErrorCodes.ERR_BUNDLE_PRICE_INVALID);
        }

        var listPriceTotal = UsdAmount.FromCents(listPriceTotalCents).Dollars;
        return Result.Success<(decimal, IReadOnlyList<BundleRevisionItemDraft>)>((listPriceTotal, items));
    }
}
