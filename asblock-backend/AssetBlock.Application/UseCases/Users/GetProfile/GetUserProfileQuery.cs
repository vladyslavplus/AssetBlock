using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Users;
using MediatR;

namespace AssetBlock.Application.UseCases.Users.GetProfile;

/// <summary>
/// When Username is null, loads the profile for CurrentUserId (e.g. GET /me). Otherwise loads by public username.
/// </summary>
public sealed record GetUserProfileQuery(string? Username, Guid? CurrentUserId) : IRequest<Result<UserProfileDto>>;
