using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.ConfirmEmailVerification;

public sealed record ConfirmEmailVerificationCommand(string ProtectedToken) : IRequest<Result>;
