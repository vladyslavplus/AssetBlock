using AssetBlock.Application.UseCases.Categories.CreateCategory;
using AssetBlock.Application.UseCases.Categories.DeleteCategory;
using AssetBlock.Application.UseCases.Categories.GetCategories;
using AssetBlock.Application.UseCases.Categories.GetCategoryById;
using AssetBlock.Application.UseCases.Categories.UpdateCategory;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.WebApi.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Categories for grouping assets (e.g., Algorithms, Shaders, UI components).
/// Admin-only write endpoints; read is public.
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

    /// <summary>
    /// Get an individual category by its ID.
    /// </summary>
    [HttpGet(ApiRoutes.Categories.BY_ID)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetCategoryByIdQuery(id), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Create a new category. Requires Admin role.
    /// </summary>
    [HttpPost(ApiRoutes.Categories.LIST)]
    [Authorize(Roles = AppRoles.ADMIN)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreateCategoryRequest request, CancellationToken cancellationToken)
    {
        var command = new CreateCategoryCommand(request.Name, request.Description, request.Slug);
        var result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Update an existing category. Requires Admin role.
    /// </summary>
    [HttpPut(ApiRoutes.Categories.BY_ID)]
    [Authorize(Roles = AppRoles.ADMIN)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateCategoryRequest request, CancellationToken cancellationToken)
    {
        var command = new UpdateCategoryCommand(id, request.Name, request.Description, request.Slug);
        var result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Delete a category. Requires Admin role.
    /// </summary>
    [HttpDelete(ApiRoutes.Categories.BY_ID)]
    [Authorize(Roles = AppRoles.ADMIN)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new DeleteCategoryCommand(id), cancellationToken);
        return MapResultToActionResult(result);
    }
}
