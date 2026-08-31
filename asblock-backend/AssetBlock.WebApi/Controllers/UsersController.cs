using AssetBlock.Application.Messaging;
using AssetBlock.Application.UseCases.Assets.EnqueueListingCopilot;
using AssetBlock.Application.UseCases.Assets.GetListingCopilotSuggestion;
using AssetBlock.Application.UseCases.Assets.GetMyAssetProcessingJobs;
using AssetBlock.Application.UseCases.Assets.GetMyAssetVersionProcessingJobs;
using AssetBlock.Application.UseCases.Assets.GetSellerAssetDetail;
using AssetBlock.Application.UseCases.Auth.ResendEmailVerification;
using AssetBlock.Application.UseCases.Users.ChangePassword;
using AssetBlock.Application.UseCases.Users.GetMyListings;
using AssetBlock.Application.UseCases.Users.GetProfile;
using AssetBlock.Application.UseCases.Users.ListMyPurchases;
using AssetBlock.Application.UseCases.Users.ListNotifications;
using AssetBlock.Application.UseCases.Users.ListSocialPlatforms;
using AssetBlock.Application.UseCases.Users.MarkAllNotificationsRead;
using AssetBlock.Application.UseCases.Users.MarkNotificationRead;
using AssetBlock.Application.UseCases.Users.MarkNotificationUnread;
using AssetBlock.Application.UseCases.Users.RequestEmailChange;
using AssetBlock.Application.UseCases.Users.UpdateProfile;
using AssetBlock.Application.UseCases.Users.UpdateSocialLinks;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.WebApi.Constants;
using AssetBlock.WebApi.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

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
        Ardalis.Result.Result<List<SocialPlatformListItemDto>> result = await Sender.Send(new ListSocialPlatformsQuery(), cancellationToken);
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
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result<PagedResult<PurchaseLibraryItemDto>> result = await Sender.Send(new GetMyPurchasesQuery(userId, request), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// List assets published by the authenticated user (seller dashboard). Uses same paging/sort as the public catalog, scoped by author.
    /// </summary>
    [HttpGet(ApiRoutes.Users.ME_ASSETS)]
    [Authorize]
    [ProducesResponseType(typeof(PagedResult<SellerAssetListItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListMyAssets([FromQuery] GetAssetsRequest request, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result<PagedResult<SellerAssetListItem>> result = await Sender.Send(new GetMyListingsQuery(userId, request), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Get owner-only seller detail for an owned asset, including processing summary.
    /// Missing, deleted, or foreign assets return 404.
    /// </summary>
    [HttpGet(ApiRoutes.Users.ME_ASSET)]
    [Authorize]
    [ProducesResponseType(typeof(SellerAssetDetailItem), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyAsset([FromRoute] Guid assetId, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result<SellerAssetDetailItem> result = await Sender.Send(new GetSellerAssetDetailQuery(assetId, userId), cancellationToken);
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
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result<PagedResult<NotificationListItemDto>> result = await Sender.Send(new GetNotificationsQuery(userId, request), cancellationToken);
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
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result<MarkAllNotificationsReadResponseDto> result = await Sender.Send(new MarkAllNotificationsReadCommand(userId), cancellationToken);
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
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result result = await Sender.Send(new MarkNotificationReadCommand(userId, id), cancellationToken);
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
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result result = await Sender.Send(new MarkNotificationUnreadCommand(userId, id), cancellationToken);
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
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result<UserProfileDto> result = await Sender.Send(new GetUserProfileQuery(null, userId), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Update the authenticated user's profile.
    /// Requires an authenticated user with a verified email address.
    /// </summary>
    [HttpPatch(ApiRoutes.Users.ME)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(typeof(UpdateUserProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateMe([FromBody] UpdateUserProfileRequest request, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        var command = new UpdateUserProfileCommand(
            userId,
            request.Username,
            request.AvatarUrl,
            request.Bio,
            request.IsPublicProfile);
        Ardalis.Result.Result<UpdateUserProfileResponse> result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Change password for the authenticated user.
    /// </summary>
    [HttpPost(ApiRoutes.Users.ME_PASSWORD)]
    [Authorize]
    [EnableRateLimiting(RateLimitingConstants.Policies.USERS_PASSWORD_CHANGE)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        var command = new ChangePasswordCommand(userId, request.CurrentPassword, request.NewPassword);
        Ardalis.Result.Result result = await Sender.Send(command, cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Resend email verification for the authenticated user (cooldown-protected).
    /// </summary>
    [HttpPost(ApiRoutes.Users.ME_EMAIL_VERIFICATION_RESEND)]
    [Authorize]
    [EnableRateLimiting(RateLimitingConstants.Policies.USERS_EMAIL_VERIFICATION_RESEND)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> ResendEmailVerification(CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result result = await Sender.Send(new ResendEmailVerificationCommand(userId), cancellationToken);
        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }

    /// <summary>
    /// Request a login-email change. Requires current password; confirmation goes to the new address.
    /// </summary>
    [HttpPost(ApiRoutes.Users.ME_EMAIL_CHANGE_REQUEST)]
    [Authorize]
    [EnableRateLimiting(RateLimitingConstants.Policies.USERS_EMAIL_CHANGE_REQUEST)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestEmailChange(
        [FromBody] RequestEmailChangeRequest request,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result result = await Sender.Send(
            new RequestEmailChangeCommand(userId, request.NewEmail, request.CurrentPassword),
            cancellationToken);
        return result.IsSuccess ? Ok() : MapResultToActionResult(result);
    }

    /// <summary>
    /// Replace the authenticated user's social links (full list).
    /// Requires an authenticated user with a verified email address.
    /// </summary>
    [HttpPut(ApiRoutes.Users.ME_SOCIALS)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateSocials([FromBody] UpdateUserSocialLinksRequest request, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        var command = new UpdateUserSocialLinksCommand(userId, request.Links);
        Ardalis.Result.Result<List<UserSocialLinkDto>> result = await Sender.Send(command, cancellationToken);
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
        Guid? currentUserId = User.GetUserIdOrNull();
        Ardalis.Result.Result<UserProfileDto> result = await Sender.Send(new GetUserProfileQuery(username, currentUserId), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Get processing jobs for an owned asset.
    /// </summary>
    [HttpGet(ApiRoutes.Users.ME_ASSET_PROCESSING_JOBS)]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<AssetProcessingJobDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyAssetProcessingJobs([FromRoute] Guid assetId, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result<IReadOnlyList<AssetProcessingJobDto>> result = await Sender.Send(new GetMyAssetProcessingJobsQuery(assetId, userId), cancellationToken);
        return MapResultToActionResult(result);
    }

    /// <summary>
    /// Get processing jobs for an owned asset version.
    /// </summary>
    [HttpGet(ApiRoutes.Users.ME_ASSET_VERSION_PROCESSING_JOBS)]
    [Authorize]
    [ProducesResponseType(typeof(IReadOnlyList<AssetProcessingJobDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetMyAssetVersionProcessingJobs([FromRoute] Guid assetVersionId, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result<IReadOnlyList<AssetProcessingJobDto>> result = await Sender.Send(new GetMyAssetVersionProcessingJobsQuery(assetVersionId, userId), cancellationToken);
        return MapResultToActionResult(result);
    }

    [HttpPost(ApiRoutes.Users.ME_ASSET_VERSION_LISTING_COPILOT)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [EnableRateLimiting(RateLimitingConstants.Policies.LISTING_COPILOT_ENQUEUE)]
    [ProducesResponseType(typeof(ListingCopilotEnqueueResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> EnqueueListingCopilot([FromRoute] Guid assetVersionId, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result<ListingCopilotEnqueueResponse> result = await Sender.Send(new EnqueueListingCopilotCommand(assetVersionId, userId), cancellationToken);
        return result.IsSuccess ? Accepted(result.Value) : MapResultToActionResult(result);
    }

    [HttpGet(ApiRoutes.Users.ME_ASSET_VERSION_LISTING_COPILOT)]
    [Authorize(Policy = AuthorizationPolicies.VERIFIED_EMAIL)]
    [ProducesResponseType(typeof(ListingCopilotSuggestionDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetListingCopilotSuggestion([FromRoute] Guid assetVersionId, CancellationToken cancellationToken)
    {
        if (!User.TryGetUserId(out Guid userId))
        {
            return UnauthorizedProblem();
        }

        Ardalis.Result.Result<ListingCopilotSuggestionDto> result = await Sender.Send(new GetListingCopilotSuggestionQuery(assetVersionId, userId), cancellationToken);
        return MapResultToActionResult(result);
    }
}
