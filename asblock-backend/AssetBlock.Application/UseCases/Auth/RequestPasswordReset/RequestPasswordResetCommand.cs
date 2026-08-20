using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Auth.RequestPasswordReset;

public sealed record RequestPasswordResetCommand(string Email) : IRequest<Result>;
