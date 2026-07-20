using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Users.RequestEmailChange;

public sealed record RequestEmailChangeCommand(Guid UserId, string NewEmail, string CurrentPassword) : IRequest<Result>;
