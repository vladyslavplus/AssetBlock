using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsOverview;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsProducts;
using AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsSales;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var (resolvedFrom, resolvedTo) = ResolveDateRange(from, to);

        var result = await Sender.Send(
            new GetSellerAnalyticsOverviewQuery(userId.Value, resolvedFrom, resolvedTo),
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
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var (resolvedFrom, resolvedTo) = ResolveDateRange(from, to);

        var request = new AnalyticsProductsRequest(resolvedFrom, resolvedTo, productType, sort, direction, page, pageSize);
        var result = await Sender.Send(
            new GetSellerAnalyticsProductsQuery(userId.Value, request),
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
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var (resolvedFrom, resolvedTo) = ResolveDateRange(from, to);

        var request = new AnalyticsSalesRequest(resolvedFrom, resolvedTo, productType, cursor, pageSize);
        var result = await Sender.Send(
            new GetSellerAnalyticsSalesQuery(userId.Value, request),
            cancellationToken);

        return MapResultToActionResult(result);
    }

    private static (DateOnly from, DateOnly to) ResolveDateRange(DateOnly? from, DateOnly? to)
    {
        var utcToday = DateOnly.FromDateTime(DateTime.UtcNow);
        return (from ?? utcToday.AddDays(-29), to ?? utcToday.AddDays(1));
    }
}
