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
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Models;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// Marketplace catalog (list/view), vendor upload, and download for purchasers.
/// </summary>
public sealed class AssetsController(
    ISender sender,
    IDownloadService downloadService,
    IOptions<FileUploadOptions> fileUploadOptions,
    ILogger<AssetsController> logger) : ApiControllerBase(sender)
{
    /// <summary>
    /// List assets with paging, search, and filters. Optional <c>authorId</c> scopes the catalog to one seller (public storefront).
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
    /// Download asset file (decrypted). Requires authentication and prior purchase (or author).
    /// Range requests are not supported for chunked AES-GCM payloads.
    /// </summary>
    [HttpGet(ApiRoutes.Assets.DOWNLOAD)]
    [Authorize]
    [EnableRateLimiting(RateLimitingConstants.Policies.ASSETS_DOWNLOAD)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status416RangeNotSatisfiable)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Download(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        if (Request.Headers.ContainsKey(HeaderNames.Range))
        {
            Response.Headers.AcceptRanges = "none";
            return StatusCode(StatusCodes.Status416RangeNotSatisfiable);
        }

        var auth = await downloadService.AuthorizeDownload(id, userId.Value, cancellationToken);
        if (auth.Status == AssetDownloadStatus.NOT_FOUND)
        {
            return ProblemFromCode(StatusCodes.Status404NotFound, ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (auth.Status == AssetDownloadStatus.FORBIDDEN)
        {
            return ProblemFromCode(StatusCodes.Status403Forbidden, ErrorCodes.ERR_PURCHASE_ACCESS_DENIED);
        }

        if (auth.Status == AssetDownloadStatus.RATE_LIMITED)
        {
            return ProblemFromCode(StatusCodes.Status429TooManyRequests, ErrorCodes.ERR_DOWNLOAD_LIMIT_EXCEEDED);
        }

        var permit = auth.Permit!;
        Response.ContentType = "application/octet-stream";
        Response.Headers.AcceptRanges = "none";
        Response.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment")
        {
            FileName = permit.FileName
        }.ToString();

        await downloadService.CopyDecrypted(permit.StorageKey, Response.Body, cancellationToken);
        return new EmptyResult();
    }

    /// <summary>
    /// Upload a new asset archive. Requires Bearer token. Multipart/form-data; form field name: "file".
    /// Allowed extensions: .zip, .7z, .rar, .tar, .tar.gz, .tgz. Max size 250 MiB.
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
            return UnauthorizedProblem();
        }

        var file = form.File;
        if (file.Length == 0)
        {
            logger.LogWarning("Upload rejected: no file for user {UserId}", userId);
            return ProblemFromCode(StatusCodes.Status400BadRequest, ErrorCodes.ERR_FILE_REQUIRED);
        }

        var maxBytes = fileUploadOptions.Value.MaxFileBytes;
        if (file.Length > maxBytes)
        {
            logger.LogWarning("Upload rejected: file too large ({Length}) for user {UserId}", file.Length, userId);
            return ProblemFromCode(StatusCodes.Status400BadRequest, ErrorCodes.ERR_FILE_TOO_LARGE);
        }

        logger.LogInformation("Upload started for user {UserId}, file {FileName}", userId, file.FileName);
        var request = new UploadAssetRequest(form.Title, form.Description, form.Price, form.CategoryId, form.DownloadLimitPerHour)
        {
            Tags = form.Tags
        };
        await using var stream = file.OpenReadStream();
        var command = new UploadAssetCommand(userId.Value, request, stream, file.FileName, file.Length);
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
            return UnauthorizedProblem();
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
            return UnauthorizedProblem();
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
            return UnauthorizedProblem();
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
            return UnauthorizedProblem();
        }

        var command = new RemoveAssetTagCommand(id, userId.Value, tagId);
        var result = await Sender.Send(command, cancellationToken);

        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }
}
