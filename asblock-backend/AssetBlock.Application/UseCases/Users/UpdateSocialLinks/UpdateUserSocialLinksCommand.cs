using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Users.UpdateSocialLinks;

public sealed record UpdateUserSocialLinksCommand(Guid UserId, List<SocialLinkInput>? Links) : IRequest<Result<List<UserSocialLinkDto>>>;
