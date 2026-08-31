using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Payments;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsSales;

internal sealed class GetSellerAnalyticsSalesQueryHandler(
    ISellerAnalyticsStore analyticsStore,
    ITypedCache cache,
    ILogger<GetSellerAnalyticsSalesQueryHandler> logger)
    : IRequestHandler<GetSellerAnalyticsSalesQuery, Result<AnalyticsSalesResult>>
{
    private static readonly TimeSpan _cacheExpiration =
        TimeSpan.FromSeconds(AnalyticsConstants.SALES_CACHE_TTL_SECONDS);

    public async Task<Result<AnalyticsSalesResult>> Handle(
        GetSellerAnalyticsSalesQuery request,
        CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.SellerAnalyticsSales(request.SellerId, request.Request);

        var cached = await cache.Get<AnalyticsSalesResult>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Seller analytics sales cache hit: {Key}", cacheKey);
            return Result.Success(cached);
        }

        var fromDto = AnalyticsRange.ToUtcStart(request.Request.From);
        var toDto = AnalyticsRange.ToUtcStart(request.Request.To);

        DateTimeOffset? cursorPurchasedAt = null;
        Guid? cursorOrderId = null;

        if (request.Request.Cursor is not null)
        {
            if (!SalesCursorCodec.TryDecode(request.Request.Cursor, out var cat, out var cid))
            {
                return Result.Invalid(
                    new ValidationError(ErrorCodes.ERR_ANALYTICS_INVALID_CURSOR, "Cursor is malformed.", "", ValidationSeverity.Error));
            }

            cursorPurchasedAt = cat;
            cursorOrderId = cid;
        }

        (IReadOnlyList<AnalyticsSaleRow> rows, var hasMore) = await analyticsStore.GetSalesPage(
            request.SellerId,
            fromDto,
            toDto,
            request.Request.ProductType,
            cursorPurchasedAt,
            cursorOrderId,
            request.Request.PageSize,
            cancellationToken);

        var items = rows.Select(r => new AnalyticsSaleItem(
            r.ProductKind,
            r.ProductId,
            r.ProductTitle,
            r.OrderId,
            r.PurchasedAt,
            r.Units,
            UsdAmount.FromDollarsRounded(r.GrossRevenue, MidpointRounding.AwayFromZero).Cents)).ToList();

        string? nextCursor = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextCursor = SalesCursorCodec.Encode(last.PurchasedAt, last.OrderId);
        }

        var result = new AnalyticsSalesResult(
            request.Request.From,
            request.Request.To,
            "UTC",
            AnalyticsConstants.CURRENCY,
            DateTimeOffset.UtcNow,
            items,
            hasMore,
            nextCursor);

        await cache.Set(cacheKey, result, _cacheExpiration, cancellationToken);

        return Result.Success(result);
    }
}
