using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.SellerAnalytics;

internal static class AnalyticsProductMapper
{
    public static AnalyticsProductItem FromAssetRow(AnalyticsAssetProductRow r) =>
        new(
            AnalyticsProductKind.ASSET,
            r.AssetId,
            r.Title,
            r.IsDeleted ? AnalyticsProductAvailability.UNAVAILABLE : AnalyticsProductAvailability.ACTIVE,
            AnalyticsRange.ToCents(r.GrossRevenue),
            AnalyticsRange.ToCents(r.DirectRevenue),
            AnalyticsRange.ToCents(r.BundleAllocatedRevenue),
            r.Orders,
            r.UnitsSold,
            r.AverageRating,
            r.ReviewCount,
            r.LatestSaleAt,
            null, null, null);

    public static AnalyticsProductItem FromBundleRow(AnalyticsBundleProductRow r)
    {
        var (currentPriceCents, listPriceCents, discountPercent) =
            MapBundlePricing(r.CurrentPrice, r.ListPriceTotal);

        return new AnalyticsProductItem(
            AnalyticsProductKind.BUNDLE,
            r.BundleId,
            r.Title,
            r.IsArchived ? AnalyticsProductAvailability.ARCHIVED : AnalyticsProductAvailability.ACTIVE,
            AnalyticsRange.ToCents(r.GrossRevenue),
            null, null,
            r.Orders,
            r.UnitsSold,
            null, null,
            r.LatestSaleAt,
            currentPriceCents,
            listPriceCents,
            discountPercent);
    }

    public static AnalyticsProductItem FromProductRow(AnalyticsProductRow r)
    {
        if (r.ProductKind == AnalyticsProductKind.ASSET)
        {
            return new AnalyticsProductItem(
                AnalyticsProductKind.ASSET,
                r.ProductId,
                r.Title,
                r.IsDeletedOrArchived
                    ? AnalyticsProductAvailability.UNAVAILABLE
                    : AnalyticsProductAvailability.ACTIVE,
                AnalyticsRange.ToCents(r.GrossRevenue),
                AnalyticsRange.ToCents(r.DirectRevenue),
                AnalyticsRange.ToCents(r.BundleAllocatedRevenue),
                r.Orders,
                r.UnitsSold,
                r.AverageRating,
                r.ReviewCount,
                r.LatestSaleAt,
                null, null, null);
        }

        var (currentPriceCents, listPriceCents, discountPercent) =
            MapBundlePricing(r.CurrentPrice, r.ListPriceTotal);

        return new AnalyticsProductItem(
            AnalyticsProductKind.BUNDLE,
            r.ProductId,
            r.Title,
            r.IsDeletedOrArchived
                ? AnalyticsProductAvailability.ARCHIVED
                : AnalyticsProductAvailability.ACTIVE,
            AnalyticsRange.ToCents(r.GrossRevenue),
            null, null,
            r.Orders,
            r.UnitsSold,
            null, null,
            r.LatestSaleAt,
            currentPriceCents,
            listPriceCents,
            discountPercent);
    }

    private static (long? CurrentPriceCents, long? ListPriceCents, decimal? DiscountPercent) MapBundlePricing(
        decimal? currentPrice,
        decimal? listPrice)
    {
        long? currentPriceCents = currentPrice.HasValue ? AnalyticsRange.ToCents(currentPrice.Value) : null;
        long? listPriceCents = listPrice.HasValue ? AnalyticsRange.ToCents(listPrice.Value) : null;
        decimal? discountPercent = null;
        if (currentPriceCents.HasValue && listPriceCents is > 0)
        {
            discountPercent = decimal.Round(
                (1m - (decimal)currentPriceCents.Value / listPriceCents.Value) * 100m,
                2, MidpointRounding.AwayFromZero);
        }

        return (currentPriceCents, listPriceCents, discountPercent);
    }
}
