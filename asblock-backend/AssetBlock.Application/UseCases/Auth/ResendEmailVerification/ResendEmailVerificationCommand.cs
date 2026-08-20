using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Auth.ResendEmailVerification;

public sealed record ResendEmailVerificationCommand(Guid UserId) : IRequest<Result>;
