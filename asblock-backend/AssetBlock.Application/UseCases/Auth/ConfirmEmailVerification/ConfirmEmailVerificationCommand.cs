using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Auth.ConfirmEmailVerification;

public sealed record ConfirmEmailVerificationCommand(string ProtectedToken) : IRequest<Result>;
