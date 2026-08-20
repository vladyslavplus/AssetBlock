using AssetBlock.Application.UseCases.Bundles.GetBundle;
using AssetBlock.Application.UseCases.Bundles.GetBundles;
using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.WebApi.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Public browse of available marketplace bundles.
/// </summary>
public sealed class BundlesController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// List public bundles with paging, search, and sorting.
    /// </summary>
    [HttpGet(ApiRoutes.Bundles.LIST)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List([FromQuery] ListBundlesRequest? request, CancellationToken cancellationToken)
    {
        request ??= new ListBundlesRequest();
        var result = await Sender.Send(new GetBundlesQuery(request), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Get a public bundle detail by id. Archived or unavailable bundles return 404.
    /// </summary>
    [HttpGet(ApiRoutes.Bundles.BY_ID)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetBundleQuery(id), cancellationToken);
        return MapResultToActionResult(result);
    }
}
