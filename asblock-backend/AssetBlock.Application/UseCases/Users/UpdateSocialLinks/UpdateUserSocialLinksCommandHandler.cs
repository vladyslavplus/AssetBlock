using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Users.UpdateSocialLinks;

internal sealed class UpdateUserSocialLinksCommandHandler(
    IUserStore userStore,
    ISocialPlatformStore socialPlatformStore,
    ILogger<UpdateUserSocialLinksCommandHandler> logger) : IRequestHandler<UpdateUserSocialLinksCommand, Result<List<UserSocialLinkDto>>>
{
    public async Task<Result<List<UserSocialLinkDto>>> Handle(UpdateUserSocialLinksCommand request, CancellationToken cancellationToken)
    {
        var platforms = await socialPlatformStore.GetAll(cancellationToken);
        var platformIds = platforms.Select(p => p.Id).ToHashSet();

        var deduped = new List<(Guid PlatformId, string Url)>();
        var seen = new HashSet<Guid>();
        foreach (var link in request.Links!)
        {
            if (!seen.Add(link.PlatformId))
            {
                continue;
            }

            deduped.Add((link.PlatformId, link.Url));
        }

        foreach (var (platformId, _) in deduped)
        {
            if (!platformIds.Contains(platformId))
            {
                logger.LogWarning("Social links update failed: unknown platform {PlatformId} for user {UserId}", platformId, request.UserId);
                return Result.NotFound(ErrorCodes.ERR_SOCIAL_PLATFORM_NOT_FOUND);
            }
        }

        var ok = await userStore.ReplaceUserSocialLinks(request.UserId, deduped, cancellationToken);
        if (!ok)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        var user = await userStore.GetByIdWithLinks(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        logger.LogInformation("User social links updated: UserId={UserId}, Count={Count}", request.UserId, deduped.Count);
        return Result.Success(MapSocialLinks(user));
    }

    private static List<UserSocialLinkDto> MapSocialLinks(User user)
    {
        return user.SocialLinks
            .OrderBy(sl => sl.Platform.Name)
            .Select(sl => new UserSocialLinkDto
            {
                Id = sl.Id,
                PlatformName = sl.Platform.Name,
                IconName = sl.Platform.IconName,
                Url = sl.Url
            })
            .ToList();
    }
}
