using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Auth.ConfirmEmailChange;

public sealed record ConfirmEmailChangeCommand(string ProtectedToken) : IRequest<Result>;
