using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Auth.ConfirmPasswordReset;

public sealed record ConfirmPasswordResetCommand(string ProtectedToken, string NewPassword) : IRequest<Result>;
