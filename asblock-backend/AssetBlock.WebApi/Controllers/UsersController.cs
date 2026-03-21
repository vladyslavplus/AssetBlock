using AssetBlock.Application.UseCases.Users.GetProfile;
using AssetBlock.Application.UseCases.Users.ListSocialPlatforms;
using AssetBlock.Application.UseCases.Users.UpdateProfile;
using AssetBlock.Application.UseCases.Users.UpdateSocialLinks;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.WebApi.Constants;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AssetBlock.WebApi.Controllers;

/// <summary>
/// User profiles and social links.
/// </summary>
public sealed class UsersController(ISender sender) : ApiControllerBase(sender)
{
    /// <summary>
    /// List supported social platforms (for profile editor).
    /// </summary>
    [HttpGet(ApiRoutes.Users.SOCIAL_PLATFORMS)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListSocialPlatforms(CancellationToken cancellationToken)
    {
        var result = await Sender.Send(new ListSocialPlatformsQuery(), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Get the authenticated user's profile (includes private profiles).
    /// </summary>
    [HttpGet(ApiRoutes.Users.ME)]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMe(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var result = await Sender.Send(new GetUserProfileQuery(null, userId), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Update the authenticated user's profile.
    /// </summary>
    [HttpPatch(ApiRoutes.Users.ME)]
    [Authorize]
    [ProducesResponseType(typeof(UpdateUserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateUserProfileRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var command = new UpdateUserProfileCommand(
            userId.Value,
            request.Username,
            request.AvatarUrl,
            request.Bio,
            request.IsPublicProfile);
        var result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Replace the authenticated user's social links (full list).
    /// </summary>
    [HttpPut(ApiRoutes.Users.ME_SOCIALS)]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSocials([FromBody] UpdateUserSocialLinksRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return Unauthorized();
        }

        var command = new UpdateUserSocialLinksCommand(userId.Value, request.Links);
        var result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Get a public profile by username. Private profiles return 404 unless the caller is the owner (use GET /me).
    /// </summary>
    [HttpGet(ApiRoutes.Users.PROFILE)]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByUsername(string username, CancellationToken cancellationToken)
    {
        var currentUserId = GetUserId();
        var result = await Sender.Send(new GetUserProfileQuery(username, currentUserId), cancellationToken);
        return MapResultToActionResult(result);
    }
}
