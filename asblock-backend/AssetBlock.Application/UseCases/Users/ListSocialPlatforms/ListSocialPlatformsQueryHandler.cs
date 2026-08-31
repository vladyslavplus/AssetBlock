using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Application.UseCases.Users.ListSocialPlatforms;

internal sealed class ListSocialPlatformsQueryHandler(ISocialPlatformStore socialPlatformStore)
    : IRequestHandler<ListSocialPlatformsQuery, Result<List<SocialPlatformListItemDto>>>
{
    public async Task<Result<List<SocialPlatformListItemDto>>> Handle(ListSocialPlatformsQuery request, CancellationToken cancellationToken)
    {
        List<SocialPlatform> platforms = await socialPlatformStore.GetAll(cancellationToken);
        var list = platforms
            .OrderBy(p => p.Name)
            .Select(p => new SocialPlatformListItemDto
            {
                Id = p.Id,
                Name = p.Name,
                IconName = p.IconName
            })
            .ToList();

        return Result.Success(list);
    }
}
