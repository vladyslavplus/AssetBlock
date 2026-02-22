using AssetBlock.Application.UseCases.Tags.CreateTag;
using AssetBlock.Application.UseCases.Tags.DeleteTag;
using AssetBlock.Application.UseCases.Tags.GetTagById;
using AssetBlock.Application.UseCases.Tags.GetTags;
using AssetBlock.Application.UseCases.Tags.UpdateTag;
using AssetBlock.Domain.Core.Dto.Tags;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

public class TagsController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// Gets a paginated list of tag names, optionally filtered by search term.
    /// </summary>
    [HttpGet(ApiRoutes.Tags.BASE)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> SearchTags([FromQuery] GetTagsRequest request, CancellationToken cancellationToken = default)
    {
        var query = new GetTagsQuery(request);
        var result = await Sender.Send(query, cancellationToken);

        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Gets a specific tag by id.
    /// </summary>
    [HttpGet(ApiRoutes.Tags.ID)]
    [AllowAnonymous]
    [ProducesResponseType(typeof(TagDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        var query = new GetTagByIdQuery(id);
        var result = await Sender.Send(query, cancellationToken);

        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Creates a new tag. Requires Admin role.
    /// </summary>
    [HttpPost(ApiRoutes.Tags.BASE)]
    [Authorize(Roles = AppRoles.ADMIN)]
    [ProducesResponseType(typeof(TagDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateTagCommand command, CancellationToken cancellationToken = default)
    {
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess
            ? CreatedAtAction(nameof(GetById), new { id = result.Value.Id }, result.Value)
            : MapResultToActionResult(result);
    }

    /// <summary>
    /// Updates an existing tag. Requires Admin role.
    /// </summary>
    [HttpPut(ApiRoutes.Tags.ID)]
    [Authorize(Roles = AppRoles.ADMIN)]
    [ProducesResponseType(typeof(TagDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTagRequest request, CancellationToken cancellationToken = default)
    {
        var command = new UpdateTagCommand(id, request.Name);
        var result = await Sender.Send(command, cancellationToken);

        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Deletes a tag. Requires Admin role.
    /// </summary>
    [HttpDelete(ApiRoutes.Tags.ID)]
    [Authorize(Roles = AppRoles.ADMIN)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var command = new DeleteTagCommand(id);
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }
}
