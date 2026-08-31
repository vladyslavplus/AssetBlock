using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Application.UseCases.Users.ListSocialPlatforms;

public sealed record ListSocialPlatformsQuery : IRequest<Result<List<SocialPlatformListItemDto>>>;
