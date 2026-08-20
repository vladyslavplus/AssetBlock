using AssetBlock.Application.UseCases.Collections.GetCollection;
using AssetBlock.Application.UseCases.Collections.GetCollections;
using AssetBlock.Domain.Core.Dto.Collections;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.WebApi.Constants;
using AssetBlock.Application.Messaging;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Public catalog for seller-curated editorial collections.
/// </summary>
public sealed class CollectionsController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// List published collections with paging, search, and sorting.
    /// </summary>
    [HttpGet(ApiRoutes.Collections.LIST)]
    [AllowAnonymous]
    [ProducesResponseType(typeof(PagedResult<CollectionListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] ListCollectionsRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetCollectionsQuery(request), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Get a published collection by id. Draft, archived, or empty-available collections return 404.
    /// </summary>
    [HttpGet(ApiRoutes.Collections.BY_ID)]
    [AllowAnonymous]
    [ProducesResponseType(typeof(CollectionDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetCollectionQuery(id), cancellationToken);
        return MapResultToActionResult(result);
    }
}
