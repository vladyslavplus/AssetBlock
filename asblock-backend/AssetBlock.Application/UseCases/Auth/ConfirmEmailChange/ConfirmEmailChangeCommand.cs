using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.ConfirmEmailChange;

public sealed record ConfirmEmailChangeCommand(string ProtectedToken) : IRequest<Result>;
