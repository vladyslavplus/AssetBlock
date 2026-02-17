using AssetBlock.Application.UseCases.Categories.GetCategories;
using AssetBlock.Domain.Dto.Categories;
using AssetBlock.WebApi.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Categories for grouping assets (e.g., Algorithms, Shaders, UI components).
/// </summary>
public sealed class CategoriesController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// Get categories with paging, sorting, and optional search.
    /// </summary>
    [HttpGet(ApiRoutes.Categories.LIST)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Get([FromQuery] GetCategoriesRequest? request, CancellationToken cancellationToken)
    {
        request ??= new GetCategoriesRequest();
        var result = await Sender.Send(new GetCategoriesQuery(request), cancellationToken);
        return MapResultToActionResult(result);
    }
}
