using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.Users.GetProfile;

internal sealed class GetUserProfileQueryHandler(
    IUserStore userStore,
    IEmailActionStore emailActionStore,
    TimeProvider? timeProvider = null) : IRequestHandler<GetUserProfileQuery, Result<UserProfileDto>>
{
    public async Task<Result<UserProfileDto>> Handle(GetUserProfileQuery request, CancellationToken cancellationToken)
    {
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        if (string.IsNullOrWhiteSpace(request.Username))
        {
            User? self = await userStore.GetByIdWithLinks(request.CurrentUserId!.Value, cancellationToken);
            if (self is null)
            {
                return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
            }

            EmailAction? pendingChange = await emailActionStore.GetCurrent(
                self.Id,
                EmailActionPurpose.EMAIL_CHANGE,
                cancellationToken);

            EmailAction? activePendingChange = pendingChange is { ConsumedAt: null } && pendingChange.ExpiresAt > now
                ? pendingChange
                : null;

            return Result.Success(MapToProfileDto(self, includeEmail: true, activePendingChange));
        }

        User? user = await userStore.GetByUsernameWithLinks(request.Username.Trim(), cancellationToken);
        if (user is null)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        var isOwner = request.CurrentUserId.HasValue && request.CurrentUserId.Value == user.Id;
        if (!user.IsPublicProfile && !isOwner)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        EmailAction? ownerPendingChange = null;
        if (isOwner)
        {
            EmailAction? pending = await emailActionStore.GetCurrent(user.Id, EmailActionPurpose.EMAIL_CHANGE, cancellationToken);
            ownerPendingChange = pending is { ConsumedAt: null } && pending.ExpiresAt > now ? pending : null;
        }

        return Result.Success(MapToProfileDto(user, includeEmail: isOwner, ownerPendingChange));
    }

    private static UserProfileDto MapToProfileDto(User user, bool includeEmail, EmailAction? pendingEmailChange = null)
    {
        var links = user.SocialLinks
            .OrderBy(sl => sl.Platform.Name)
            .Select(sl => new UserSocialLinkDto
            {
                Id = sl.Id,
                PlatformName = sl.Platform.Name,
                IconName = sl.Platform.IconName,
                Url = sl.Url
            })
            .ToList();

        return new UserProfileDto
        {
            Id = user.Id,
            Username = user.Username,
            Email = includeEmail ? user.Email : null,
            EmailVerifiedAt = includeEmail ? user.EmailVerifiedAt : null,
            PendingEmail = includeEmail ? pendingEmailChange?.TargetEmail : null,
            PendingEmailChangeExpiresAt = includeEmail ? pendingEmailChange?.ExpiresAt : null,
            AvatarUrl = user.AvatarUrl,
            Bio = user.Bio,
            IsPublicProfile = user.IsPublicProfile,
            CreatedAt = user.CreatedAt,
            SocialLinks = links,
            Role = includeEmail ? user.Role : null
        };
    }
}
