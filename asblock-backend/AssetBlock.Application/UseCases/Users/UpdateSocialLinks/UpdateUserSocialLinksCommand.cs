using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Application.UseCases.Users.UpdateSocialLinks;

public sealed record UpdateUserSocialLinksCommand(Guid UserId, List<SocialLinkInput>? Links) : IRequest<Result<List<UserSocialLinkDto>>>;
