using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Users.GetProfile;

internal sealed class GetUserProfileQueryHandler(IUserStore userStore) : IRequestHandler<GetUserProfileQuery, Result<UserProfileDto>>
{
    public async Task<Result<UserProfileDto>> Handle(GetUserProfileQuery request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
        {
            var self = await userStore.GetByIdWithLinks(request.CurrentUserId!.Value, cancellationToken);
            return self is null ? Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND) : Result.Success(MapToProfileDto(self));
        }

        var user = await userStore.GetByUsernameWithLinks(request.Username.Trim(), cancellationToken);
        if (user is null)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        var isOwner = request.CurrentUserId.HasValue && request.CurrentUserId.Value == user.Id;
        if (!user.IsPublicProfile && !isOwner)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        return Result.Success(MapToProfileDto(user));
    }

    private static UserProfileDto MapToProfileDto(User user)
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
            AvatarUrl = user.AvatarUrl,
            Bio = user.Bio,
            IsPublicProfile = user.IsPublicProfile,
            CreatedAt = user.CreatedAt,
            SocialLinks = links
        };
    }
}
