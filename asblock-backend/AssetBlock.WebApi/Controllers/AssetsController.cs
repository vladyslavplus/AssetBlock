using AssetBlock.Application.UseCases.Assets.AddAssetTag;
using AssetBlock.Application.UseCases.Assets.DeleteAsset;
using AssetBlock.Application.UseCases.Assets.GetAssetById;
using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Application.UseCases.Assets.RemoveAssetTag;
using AssetBlock.Application.UseCases.Assets.UpdateAsset;
using AssetBlock.Application.UseCases.Assets.UploadAsset;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Models;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Marketplace catalog (list/view), vendor upload, and download for purchasers.
/// </summary>
public sealed class AssetsController(ISender sender, IDownloadService downloadService, ILogger<AssetsController> logger) : ApiControllerBase(sender)
{
    /// <summary>
    /// List assets with paging, search, and filters.
    /// </summary>
    [HttpGet(ApiRoutes.Assets.LIST)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromQuery] GetAssetsRequest request, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetAssetsQuery(request), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Get a single asset by id (catalog view).
    /// </summary>
    [HttpGet(ApiRoutes.Assets.ID)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new GetAssetByIdQuery(id), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Download asset file (decrypted). Requires authentication and prior purchase.
    /// </summary>
    [HttpGet(ApiRoutes.Assets.DOWNLOAD)]
    [Authorize]
    [EnableRateLimiting(RateLimitingConstants.Policies.ASSETS_DOWNLOAD)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var streamResult = await downloadService.GetAssetStream(id, userId.Value, cancellationToken);
        if (streamResult.Status == AssetDownloadStatus.NotFound)
        {
            return NotFound(new { errors = new[] { new { identifier = ErrorCodes.ERR_ASSET_NOT_FOUND, message = ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_ASSET_NOT_FOUND) } } });
        }
        if (streamResult.Status == AssetDownloadStatus.Forbidden)
        {
            return StatusCode(403, new { errors = new[] { new { identifier = ErrorCodes.ERR_PURCHASE_ACCESS_DENIED, message = ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_PURCHASE_ACCESS_DENIED) } } });
        }
        if (streamResult.Status == AssetDownloadStatus.RateLimited)
        {
            return StatusCode(429, new { errors = new[] { new { identifier = ErrorCodes.ERR_DOWNLOAD_LIMIT_EXCEEDED, message = ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_DOWNLOAD_LIMIT_EXCEEDED) } } });
        }

        return File(streamResult.Content!, "application/octet-stream", streamResult.FileName!);
    }

    /// <summary>
    /// Upload a new asset. Requires Bearer token. Multipart/form-data with title, description, price, categoryId and file (any extension). Form field name: "file".
    /// </summary>
    [HttpPost(ApiRoutes.Assets.UPLOAD)]
    [Authorize]
    [EnableRateLimiting(RateLimitingConstants.Policies.ASSETS_UPLOAD)]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Upload(
        [FromForm] UploadAssetFormWithFile form,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            logger.LogWarning("Upload rejected: no authenticated user (missing or invalid Bearer token)");
            return Unauthorized();
        }

        var file = form.File;
        if (file.Length == 0)
        {
            logger.LogWarning("Upload rejected: no file for user {UserId}", userId);
            return BadRequest(new { errors = new[] { new { identifier = ErrorCodes.ERR_FILE_REQUIRED, message = ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_FILE_REQUIRED) } } });
        }

        logger.LogInformation("Upload started for user {UserId}, file {FileName}", userId, file.FileName);
        var request = new UploadAssetRequest(form.Title, form.Description, form.Price, form.CategoryId, form.DownloadLimitPerHour)
        {
            Tags = form.Tags
        };
        await using var stream = file.OpenReadStream();
        var command = new UploadAssetCommand(userId.Value, request, stream, file.FileName);
        var result = await Sender.Send(command, cancellationToken);

        if (result.IsSuccess)
        {
            logger.LogInformation("Upload succeeded: AssetId={AssetId}, UserId={UserId}", result.Value, userId);
            return Ok(new { id = result.Value });
        }

        logger.LogWarning("Upload failed for user {UserId}: {Status} {Errors}", userId, result.Status, string.Join("; ", result.Errors));
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Partial update of an asset (title, description, price, categoryId). Requires Bearer token. Only the author can update.
    /// </summary>
    [HttpPatch(ApiRoutes.Assets.ID)]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateAssetRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var command = new UpdateAssetCommand(id, userId.Value, request.Title, request.Description, request.Price, request.CategoryId);
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }

    /// <summary>
    /// Delete an asset. Requires Bearer token. Only the author can delete it.
    /// </summary>
    [HttpDelete(ApiRoutes.Assets.ID)]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var command = new DeleteAssetCommand(id, userId.Value);
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }

    /// <summary>
    /// Adds a tag to an asset. Requires Bearer token. Only the author can manage tags.
    /// </summary>
    [HttpPost(ApiRoutes.Assets.TAGS)]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AddTag(Guid id, [FromBody] AddAssetTagRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var command = new AddAssetTagCommand(id, userId.Value, request.Name);
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok(result.Value) : MapResultToActionResult(result);
    }

    /// <summary>
    /// Removes a tag from an asset. Requires Bearer token. Only the author can manage tags.
    /// </summary>
    [HttpDelete(ApiRoutes.Assets.TAGS_ID)]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveTag(Guid id, Guid tagId, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var command = new RemoveAssetTagCommand(id, userId.Value, tagId);
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }
}
