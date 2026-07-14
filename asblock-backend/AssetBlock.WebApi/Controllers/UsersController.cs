using AssetBlock.Application.UseCases.Assets.GetAssets;
using AssetBlock.Application.UseCases.Users.ChangePassword;
using AssetBlock.Application.UseCases.Users.GetProfile;
using AssetBlock.Application.UseCases.Users.ListMyPurchases;
using AssetBlock.Application.UseCases.Users.ListNotifications;
using AssetBlock.Application.UseCases.Users.MarkAllNotificationsRead;
using AssetBlock.Application.UseCases.Users.ListSocialPlatforms;
using AssetBlock.Application.UseCases.Users.MarkNotificationRead;
using AssetBlock.Application.UseCases.Users.MarkNotificationUnread;
using AssetBlock.Application.UseCases.Users.UpdateProfile;
using AssetBlock.Application.UseCases.Users.UpdateSocialLinks;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Assets;
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
    /// List purchased assets for the current user (library). Newest purchase first by default.
    /// </summary>
    [HttpGet(ApiRoutes.Users.ME_PURCHASES)]
    [Authorize]
    [ProducesResponseType(typeof(PagedResult<PurchaseLibraryItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListMyPurchases([FromQuery] ListMyPurchasesRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await Sender.Send(new GetMyPurchasesQuery(userId.Value, request), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// List assets published by the authenticated user (seller dashboard). Uses same paging/sort as the public catalog, scoped by author.
    /// </summary>
    [HttpGet(ApiRoutes.Users.ME_ASSETS)]
    [Authorize]
    [ProducesResponseType(typeof(PagedResult<AssetListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListMyAssets([FromQuery] GetAssetsRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var scoped = request with { AuthorId = userId.Value };
        var result = await Sender.Send(new GetAssetsQuery(scoped), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// List notifications for the current user (newest first by default).
    /// </summary>
    [HttpGet(ApiRoutes.Users.ME_NOTIFICATIONS)]
    [Authorize]
    [ProducesResponseType(typeof(PagedResult<NotificationListItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListMyNotifications([FromQuery] GetNotificationsRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await Sender.Send(new GetNotificationsQuery(userId.Value, request), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Mark all notifications as read for the current user.
    /// </summary>
    [HttpPost(ApiRoutes.Users.ME_NOTIFICATIONS_READ_ALL)]
    [Authorize]
    [ProducesResponseType(typeof(MarkAllNotificationsReadResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> MarkAllMyNotificationsRead(CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await Sender.Send(new MarkAllNotificationsReadCommand(userId.Value), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Mark a notification as read.
    /// </summary>
    [HttpPatch(ApiRoutes.Users.ME_NOTIFICATION_READ)]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkMyNotificationRead(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await Sender.Send(new MarkNotificationReadCommand(userId.Value, id), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Mark a notification as unread.
    /// </summary>
    [HttpPatch(ApiRoutes.Users.ME_NOTIFICATION_UNREAD)]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MarkMyNotificationUnread(Guid id, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var result = await Sender.Send(new MarkNotificationUnreadCommand(userId.Value, id), cancellationToken);
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
            return UnauthorizedProblem();
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
            return UnauthorizedProblem();
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
    /// Change password for the authenticated user.
    /// </summary>
    [HttpPost(ApiRoutes.Users.ME_PASSWORD)]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        var userId = GetUserId();
        if (userId is null)
        {
            return UnauthorizedProblem();
        }

        var command = new ChangePasswordCommand(userId.Value, request.CurrentPassword, request.NewPassword);
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
            return UnauthorizedProblem();
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
