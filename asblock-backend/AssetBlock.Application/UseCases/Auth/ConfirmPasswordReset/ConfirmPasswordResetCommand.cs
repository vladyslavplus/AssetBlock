using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.ConfirmPasswordReset;

public sealed record ConfirmPasswordResetCommand(string ProtectedToken, string NewPassword) : IRequest<Result>;
