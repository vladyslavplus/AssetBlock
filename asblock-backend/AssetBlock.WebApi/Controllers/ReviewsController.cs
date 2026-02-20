using AssetBlock.Application.UseCases.Reviews.CreateReview;
using AssetBlock.Application.UseCases.Reviews.DeleteReview;
using AssetBlock.Application.UseCases.Reviews.GetReviewById;
using AssetBlock.Application.UseCases.Reviews.GetReviews;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.WebApi.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

public class ReviewsController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// Creates a new review for the specified asset.
    /// </summary>
    [HttpPost(ApiRoutes.Reviews.CREATE_FOR_ASSET)]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateReview(Guid assetId, [FromBody] CreateReviewRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId()!.Value;
        var command = new CreateReviewCommand(assetId, userId, request.Rating, request.Comment);

        var result = await Sender.Send(command, cancellationToken);
        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }

    /// <summary>
    /// Retrieves a paginated list of reviews for the specified asset.
    /// </summary>
    [HttpGet(ApiRoutes.Reviews.LIST_FOR_ASSET)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetReviews(Guid assetId, [FromQuery] GetReviewsRequest request, CancellationToken cancellationToken)
    {
        var query = new GetReviewsQuery(assetId, request);
        var result = await Sender.Send(query, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Retrieves a specific review by its ID.
    /// </summary>
    [HttpGet(ApiRoutes.Reviews.BY_ID)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReviewById(Guid id, CancellationToken cancellationToken)
    {
        var query = new GetReviewByIdQuery(id);
        var result = await Sender.Send(query, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Deletes a specific review by its ID (Admin only).
    /// </summary>
    [HttpDelete(ApiRoutes.Reviews.BY_ID)]
    [Authorize(Roles = AppRoles.ADMIN)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> DeleteReview(Guid id, CancellationToken cancellationToken)
    {
        var command = new DeleteReviewCommand(id);
        var result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }
}
