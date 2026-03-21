using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Exceptions;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Users.UpdateProfile;

internal sealed class UpdateUserProfileCommandHandler(
    IUserStore userStore,
    ILogger<UpdateUserProfileCommandHandler> logger) : IRequestHandler<UpdateUserProfileCommand, Result<UpdateUserProfileResponse>>
{
    public async Task<Result<UpdateUserProfileResponse>> Handle(UpdateUserProfileCommand request, CancellationToken cancellationToken)
    {
        var user = await userStore.GetByIdForUpdate(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        if (request.Username is not null)
        {
            user.Username = request.Username.Trim();
        }

        if (request.AvatarUrl is not null)
        {
            user.AvatarUrl = string.IsNullOrWhiteSpace(request.AvatarUrl) ? null : request.AvatarUrl.Trim();
        }

        if (request.Bio is not null)
        {
            user.Bio = string.IsNullOrWhiteSpace(request.Bio) ? null : request.Bio.Trim();
        }

        if (request.IsPublicProfile is not null)
        {
            user.IsPublicProfile = request.IsPublicProfile.Value;
        }

        try
        {
            await userStore.Update(user, cancellationToken);
        }
        catch (DuplicateUsernameException)
        {
            logger.LogWarning("Profile update failed: username already exists for user {UserId}", request.UserId);
            return Result.Conflict(ErrorCodes.ERR_USERNAME_ALREADY_EXISTS);
        }

        logger.LogInformation("User profile updated: UserId={UserId}", request.UserId);
        return Result.Success(new UpdateUserProfileResponse
        {
            Username = user.Username,
            AvatarUrl = user.AvatarUrl,
            Bio = user.Bio,
            IsPublicProfile = user.IsPublicProfile
        });
    }
}
