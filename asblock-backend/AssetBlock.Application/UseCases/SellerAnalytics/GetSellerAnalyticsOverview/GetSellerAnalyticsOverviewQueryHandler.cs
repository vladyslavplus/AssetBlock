using Ardalis.Result;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsOverview;

internal sealed class GetSellerAnalyticsOverviewQueryHandler(
    ISellerAnalyticsStore analyticsStore,
    ITypedCache cache,
    ILogger<GetSellerAnalyticsOverviewQueryHandler> logger)
    : IRequestHandler<GetSellerAnalyticsOverviewQuery, Result<SellerAnalyticsOverviewDto>>
{
    private static readonly TimeSpan _cacheExpiration =
        TimeSpan.FromSeconds(AnalyticsConstants.OVERVIEW_CACHE_TTL_SECONDS);

    public async Task<Result<SellerAnalyticsOverviewDto>> Handle(
        GetSellerAnalyticsOverviewQuery request,
        CancellationToken cancellationToken)
    {
        var cacheKey = CacheKeys.SellerAnalyticsOverview(request.SellerId, request.From, request.To);

        var cached = await cache.Get<SellerAnalyticsOverviewDto>(cacheKey, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Seller analytics overview cache hit: {Key}", cacheKey);
            return Result.Success(cached);
        }

        var fromDto = AnalyticsRange.ToUtcStart(request.From);
        var toDto = AnalyticsRange.ToUtcStart(request.To);

        (DateOnly compFrom, DateOnly compTo) = AnalyticsRange.ComparisonPeriod(request.From, request.To);
        var compFromDto = AnalyticsRange.ToUtcStart(compFrom);
        var compToDto = AnalyticsRange.ToUtcStart(compTo);

        var granularity = AnalyticsRange.Granularity(request.From, request.To);

        var snapshot = await analyticsStore.GetOverviewSnapshot(
            request.SellerId,
            fromDto,
            toDto,
            compFromDto,
            compToDto,
            AnalyticsConstants.OVERVIEW_TOP_N,
            granularity,
            cancellationToken);

        var overviewDto = SellerAnalyticsOverviewMapper.MapOverview(
            snapshot,
            request.From,
            request.To,
            compFrom,
            compTo,
            granularity);

        await cache.Set(cacheKey, overviewDto, _cacheExpiration, cancellationToken);

        return Result.Success(overviewDto);
    }
}
