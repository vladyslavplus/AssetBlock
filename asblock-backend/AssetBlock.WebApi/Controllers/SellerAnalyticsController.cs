using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsAssetDetail;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsBundleDetail;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsCollections;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsOverview;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsProducts;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsSales;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.Results;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Seller analytics dashboard — revenue, customer, and product metrics.
/// Absolute route avoids inheriting api/[controller] from ApiControllerBase.
/// </summary>
[Route(ApiRoutes.SellerAnalytics.BASE)]
[Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
public sealed class SellerAnalyticsController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// Overview KPIs, series chart, and top-5 assets/bundles for the seller.
    /// </summary>
    [HttpGet(ApiRoutes.SellerAnalytics.OVERVIEW)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetOverview(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        (DateOnly resolvedFrom, DateOnly resolvedTo) = ResolveDateRange(from, to);

        Result<SellerAnalyticsOverviewDto> result = await Sender.Send(
            new GetSellerAnalyticsOverviewQuery(userId, resolvedFrom, resolvedTo),
            cancellationToken);

        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Paginated product performance table (assets and/or bundles).
    /// </summary>
    [HttpGet(ApiRoutes.SellerAnalytics.PRODUCTS)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetProducts(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] AnalyticsProductTypeFilter productType = AnalyticsProductTypeFilter.ALL,
        [FromQuery] AnalyticsProductSort sort = AnalyticsProductSort.REVENUE,
        [FromQuery] AnalyticsSortDirection direction = AnalyticsSortDirection.DESC,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = AnalyticsConstants.DEFAULT_PRODUCTS_PAGE_SIZE,
        CancellationToken cancellationToken = default)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        (DateOnly resolvedFrom, DateOnly resolvedTo) = ResolveDateRange(from, to);

        var request = new AnalyticsProductsRequest(resolvedFrom, resolvedTo, productType, sort, direction, page, pageSize);
        Result<AnalyticsProductsResult> result = await Sender.Send(
            new GetSellerAnalyticsProductsQuery(userId, request),
            cancellationToken);

        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Asset drill-down analytics for a seller-owned product.
    /// </summary>
    [HttpGet(ApiRoutes.SellerAnalytics.PRODUCT_ASSET_BY_ID)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAssetDetail(
        Guid id,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken = default)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        (DateOnly resolvedFrom, DateOnly resolvedTo) = ResolveDateRange(from, to);
        Result<AnalyticsAssetDetailDto> result = await Sender.Send(
            new GetSellerAnalyticsAssetDetailQuery(userId, id, resolvedFrom, resolvedTo),
            cancellationToken);

        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Bundle drill-down analytics for a seller-owned product.
    /// </summary>
    [HttpGet(ApiRoutes.SellerAnalytics.PRODUCT_BUNDLE_BY_ID)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBundleDetail(
        Guid id,
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        CancellationToken cancellationToken = default)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        (DateOnly resolvedFrom, DateOnly resolvedTo) = ResolveDateRange(from, to);
        Result<AnalyticsBundleDetailDto> result = await Sender.Send(
            new GetSellerAnalyticsBundleDetailQuery(userId, id, resolvedFrom, resolvedTo),
            cancellationToken);

        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Paginated collection performance for the seller.
    /// </summary>
    [HttpGet(ApiRoutes.SellerAnalytics.COLLECTIONS)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetCollections(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] AnalyticsCollectionSort sort = AnalyticsCollectionSort.VIEWS,
        [FromQuery] AnalyticsSortDirection direction = AnalyticsSortDirection.DESC,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = AnalyticsConstants.DEFAULT_COLLECTIONS_PAGE_SIZE,
        CancellationToken cancellationToken = default)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        (DateOnly resolvedFrom, DateOnly resolvedTo) = ResolveDateRange(from, to);
        var request = new AnalyticsCollectionsRequest(resolvedFrom, resolvedTo, sort, direction, page, pageSize);
        Result<AnalyticsCollectionsResult> result = await Sender.Send(
            new GetSellerAnalyticsCollectionsQuery(userId, request),
            cancellationToken);

        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Keyset-paginated sales feed (newest first). No buyer or Stripe data exposed.
    /// </summary>
    [HttpGet(ApiRoutes.SellerAnalytics.SALES)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> GetSales(
        [FromQuery] DateOnly? from,
        [FromQuery] DateOnly? to,
        [FromQuery] AnalyticsProductTypeFilter productType = AnalyticsProductTypeFilter.ALL,
        [FromQuery] string? cursor = null,
        [FromQuery] int pageSize = AnalyticsConstants.DEFAULT_SALES_PAGE_SIZE,
        CancellationToken cancellationToken = default)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        (DateOnly resolvedFrom, DateOnly resolvedTo) = ResolveDateRange(from, to);

        var request = new AnalyticsSalesRequest(resolvedFrom, resolvedTo, productType, cursor, pageSize);
        Result<AnalyticsSalesResult> result = await Sender.Send(
            new GetSellerAnalyticsSalesQuery(userId, request),
            cancellationToken);

        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Streams a CSV export of seller sales for the requested period. No buyer or Stripe data exposed.
    /// </summary>
    [HttpGet(ApiRoutes.SellerAnalytics.SALES_EXPORT)]
    [EnableRateLimiting(RateLimitingConstants.Policies.SELLER_ANALYTICS_SALES_EXPORT)]
    [Produces("text/csv")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public IActionResult ExportSales(
        [FromQuery] DateOnly from,
        [FromQuery] DateOnly to,
        [FromQuery] AnalyticsProductTypeFilter productType = AnalyticsProductTypeFilter.ALL,
        CancellationToken cancellationToken = default)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        return new SellerAnalyticsSalesCsvExportResult(
            userId,
            from,
            to,
            productType,
            Sender);
    }

    private static (DateOnly from, DateOnly to) ResolveDateRange(DateOnly? from, DateOnly? to)
    {
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        return (from ?? utcToday.AddDays(-29), to ?? utcToday.AddDays(1));
    }
}
