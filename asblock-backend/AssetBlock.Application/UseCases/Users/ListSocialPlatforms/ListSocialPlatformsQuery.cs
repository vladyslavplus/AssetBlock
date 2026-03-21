using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Users;
using MediatR;

namespace AssetBlock.Application.UseCases.Users.ListSocialPlatforms;

public sealed record ListSocialPlatformsQuery : IRequest<Result<List<SocialPlatformListItemDto>>>;
