using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Bundles.ArchiveBundle;
using AssetBlock.Application.UseCases.Bundles.CreateBundle;
using AssetBlock.Application.UseCases.Bundles.GetMyBundle;
using AssetBlock.Application.UseCases.Bundles.GetMyBundles;
using AssetBlock.Application.UseCases.Bundles.RestoreBundle;
using AssetBlock.Application.UseCases.Bundles.ReviseBundle;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.ProblemDetails;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Seller-owned bundle management. Absolute route avoids inheriting api/[controller].
/// </summary>
[ApiController]
[Route(ApiRoutes.SellerBundles.BASE)]
[Authorize]
[Produces("application/json")]
public sealed class SellerBundlesController(ISender sender) : ControllerBase
{
    /// <summary>
    /// List bundles owned by the authenticated seller.
    /// </summary>
    [HttpGet(ApiRoutes.SellerBundles.LIST)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List([FromQuery] ListMyBundlesRequest? request, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        request ??= new ListMyBundlesRequest();
        Result<Domain.Core.Dto.Paging.PagedResult<BundleListItemDto>> result = await sender.Send(new GetMyBundlesQuery(userId, request), cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }

    /// <summary>
    /// Get seller detail for one owned bundle (includes unavailable item reasons).
    /// </summary>
    [HttpGet(ApiRoutes.SellerBundles.BY_ID)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Result<BundleDetailDto> result = await sender.Send(new GetMyBundleQuery(id, userId), cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }

    /// <summary>
    /// Create a bundle with revision 1 from a full definition.
    /// </summary>
    [HttpPost(ApiRoutes.SellerBundles.LIST)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateBundleRequest request, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        var command = new CreateBundleCommand(
            userId,
            request.Title,
            request.Description,
            request.Price,
            request.AssetIds);
        Result<CreateBundleResponse> result = await sender.Send(command, cancellationToken);
        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value)
            : ResultProblemDetailsMapper.Map(HttpContext, result);
    }

    /// <summary>
    /// Publish the next immutable revision for an owned bundle.
    /// </summary>
    [HttpPut(ApiRoutes.SellerBundles.BY_ID)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Revise(Guid id, [FromBody] ReviseBundleRequest request, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        var command = new ReviseBundleCommand(
            id,
            userId,
            request.Title,
            request.Description,
            request.Price,
            request.AssetIds);
        Result<ReviseBundleResponse> result = await sender.Send(command, cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }

    /// <summary>
    /// Archive an owned bundle (hides from public catalog / new checkout).
    /// </summary>
    [HttpPost(ApiRoutes.SellerBundles.ARCHIVE)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Result result = await sender.Send(new ArchiveBundleCommand(id, userId), cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }

    /// <summary>
    /// Restore an archived bundle after re-validating current revision assets.
    /// </summary>
    [HttpPost(ApiRoutes.SellerBundles.RESTORE)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Result result = await sender.Send(new RestoreBundleCommand(id, userId), cancellationToken);
        return ResultProblemDetailsMapper.Map(HttpContext, result);
    }

    private IActionResult UnauthorizedProblem() =>
        AssetBlockProblemDetails.ToActionResult(
            AssetBlockProblemDetails.Create(HttpContext, StatusCodes.Status401Unauthorized, ErrorCodes.ERR_AUTH_TOKEN_INVALID));
}
