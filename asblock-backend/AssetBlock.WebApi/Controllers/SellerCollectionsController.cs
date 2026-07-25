using System.Security.Claims;
using Ardalis.Result;
using AssetBlock.Application.UseCases.Collections.AddCollectionItem;
using AssetBlock.Application.UseCases.Collections.ArchiveCollection;
using AssetBlock.Application.UseCases.Collections.CreateCollection;
using AssetBlock.Application.UseCases.Collections.GetMyCollection;
using AssetBlock.Application.UseCases.Collections.GetMyCollections;
using AssetBlock.Application.UseCases.Collections.PublishCollection;
using AssetBlock.Application.UseCases.Collections.RemoveCollectionItem;
using AssetBlock.Application.UseCases.Collections.ReorderCollectionItems;
using AssetBlock.Application.UseCases.Collections.RestoreCollection;
using AssetBlock.Application.UseCases.Collections.UpdateCollection;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Collections;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.ProblemDetails;
using Asp.Versioning;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Seller management for editorial collections (draft / publish / archive).
/// Absolute route avoids inheriting api/[controller] from ApiControllerBase.
/// </summary>
[ApiController]
[ApiVersion("1.0")]
[Route("api/seller/collections")]
[Produces("application/json")]
public sealed class SellerCollectionsController(ISender sender) : ControllerBase
{
    /// <summary>
    /// List collections owned by the authenticated seller.
    /// </summary>
    [HttpGet(ApiRoutes.SellerCollections.LIST)]
    [Authorize]
    [ProducesResponseType(typeof(AssetBlock.Domain.Core.Dto.Paging.PagedResult<CollectionListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List([FromQuery] ListMyCollectionsRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await sender.Send(new GetMyCollectionsQuery(userId.Value, request), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Get a collection owned by the authenticated seller (includes unavailable items).
    /// </summary>
    [HttpGet(ApiRoutes.SellerCollections.BY_ID)]
    [Authorize]
    [ProducesResponseType(typeof(CollectionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await sender.Send(new GetMyCollectionQuery(id, userId.Value), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Create a draft collection. Requires a verified email address.
    /// </summary>
    [HttpPost(ApiRoutes.SellerCollections.LIST)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(typeof(CreateCollectionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateCollectionRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await sender.Send(
            new CreateCollectionCommand(userId.Value, request.Title, request.Description),
            cancellationToken);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value)
            : MapResultToActionResult(result);
    }

    /// <summary>
    /// Update collection title and description. Requires a verified email address.
    /// </summary>
    [HttpPatch(ApiRoutes.SellerCollections.BY_ID)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        Guid id,
        [FromBody] UpdateCollectionRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await sender.Send(
            new UpdateCollectionCommand(id, userId.Value, request.Title, request.Description),
            cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Add an owned active asset to a collection. Requires a verified email address.
    /// </summary>
    [HttpPost(ApiRoutes.SellerCollections.ITEMS)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddItem(
        Guid id,
        [FromBody] AddCollectionItemRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await sender.Send(
            new AddCollectionItemCommand(id, userId.Value, request.AssetId),
            cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Remove an asset from a collection. Requires a verified email address.
    /// </summary>
    [HttpDelete(ApiRoutes.SellerCollections.ITEM_BY_ASSET)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> RemoveItem(Guid id, Guid assetId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await sender.Send(
            new RemoveCollectionItemCommand(id, userId.Value, assetId),
            cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Reorder collection items. AssetIds must be the exact current membership set. Requires a verified email address.
    /// </summary>
    [HttpPut(ApiRoutes.SellerCollections.ITEMS_ORDER)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> ReorderItems(
        Guid id,
        [FromBody] ReorderCollectionItemsRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await sender.Send(
            new ReorderCollectionItemsCommand(id, userId.Value, request.AssetIds),
            cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Publish a draft collection. Requires at least one active item and a verified email address.
    /// </summary>
    [HttpPost(ApiRoutes.SellerCollections.PUBLISH)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Publish(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await sender.Send(new PublishCollectionCommand(id, userId.Value), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Archive a published collection. Requires a verified email address.
    /// </summary>
    [HttpPost(ApiRoutes.SellerCollections.ARCHIVE)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Archive(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await sender.Send(new ArchiveCollectionCommand(id, userId.Value), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Restore an archived collection to draft. Requires a verified email address.
    /// </summary>
    [HttpPost(ApiRoutes.SellerCollections.RESTORE)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Restore(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await sender.Send(new RestoreCollectionCommand(id, userId.Value), cancellationToken);
        return MapResultToActionResult(result);
    }

    private Guid? GetUserId()
    {
        var value = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return Guid.TryParse(value, out var id) ? id : null;
    }

    private IActionResult MapResultToActionResult<T>(Result<T> result) =>
        ResultProblemDetailsMapper.Map(HttpContext, result);

    private IActionResult MapResultToActionResult(Result result) =>
        ResultProblemDetailsMapper.Map(HttpContext, result);

    private IActionResult UnauthorizedProblem() =>
        ProblemFromCode(StatusCodes.Status401Unauthorized, ErrorCodes.ERR_AUTH_TOKEN_INVALID);

    private IActionResult ProblemFromCode(int status, string code) =>
        AssetBlockProblemDetails.ToActionResult(AssetBlockProblemDetails.Create(HttpContext, status, code));
}
