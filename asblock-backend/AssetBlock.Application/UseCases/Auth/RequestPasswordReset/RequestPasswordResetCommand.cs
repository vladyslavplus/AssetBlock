using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.RequestPasswordReset;

public sealed record RequestPasswordResetCommand(string Email) : IRequest<Result>;
