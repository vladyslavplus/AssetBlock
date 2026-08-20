using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Users.RequestEmailChange;

public sealed record RequestEmailChangeCommand(Guid UserId, string NewEmail, string CurrentPassword) : IRequest<Result>;
