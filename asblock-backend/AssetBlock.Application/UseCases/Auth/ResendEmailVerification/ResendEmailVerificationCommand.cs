using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.ResendEmailVerification;

public sealed record ResendEmailVerificationCommand(Guid UserId) : IRequest<Result>;
