using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsProducts;

internal sealed class GetSellerAnalyticsProductsQueryHandler(
    ISellerAnalyticsStore analyticsStore,
    ITypedCache cache,
    ILogger<GetSellerAnalyticsProductsQueryHandler> logger)
    : IRequestHandler<GetSellerAnalyticsProductsQuery, Result<AnalyticsProductsResult>>
{
    private static readonly TimeSpan _cacheExpiration =
        TimeSpan.FromSeconds(AnalyticsConstants.PRODUCTS_CACHE_TTL_SECONDS);

    public async Task<Result<AnalyticsProductsResult>> Handle(
        GetSellerAnalyticsProductsQuery request,
        CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.SellerAnalyticsProducts(request.SellerId, request.Request);

        var cached = await cache.Get<AnalyticsProductsResult>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Seller analytics products cache hit: {Key}", cacheKey);
            return Result.Success(cached);
        }

        var fromDto = AnalyticsRange.ToUtcStart(request.Request.From);
        var toDto = AnalyticsRange.ToUtcStart(request.Request.To);
        var req = request.Request;

        (IReadOnlyList<AnalyticsProductRow> rows, var total) = await analyticsStore.GetProductsPage(
            request.SellerId,
            fromDto,
            toDto,
            req.ProductType,
            req.Page,
            req.PageSize,
            req.Sort,
            req.Direction,
            cancellationToken);

        var result = new AnalyticsProductsResult(
            req.From,
            req.To,
            "UTC",
            AnalyticsConstants.CURRENCY,
            DateTimeOffset.UtcNow,
            rows.Select(AnalyticsProductMapper.FromProductRow).ToList(),
            total,
            req.Page,
            req.PageSize);

        await cache.Set(cacheKey, result, _cacheExpiration, cancellationToken);

        return Result.Success(result);
    }
}
