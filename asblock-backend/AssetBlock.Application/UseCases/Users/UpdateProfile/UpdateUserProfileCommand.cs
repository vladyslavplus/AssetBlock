using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Users.UpdateProfile;

public sealed record UpdateUserProfileCommand(
    Guid UserId,
    string? Username,
    string? AvatarUrl,
    string? Bio,
    bool? IsPublicProfile) : IRequest<Result<UpdateUserProfileResponse>>;
