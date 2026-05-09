using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Users.ChangePassword;

public sealed record ChangePasswordCommand(Guid UserId, string CurrentPassword, string NewPassword) : IRequest<Result>;
