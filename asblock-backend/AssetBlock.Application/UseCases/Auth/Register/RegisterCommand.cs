using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Auth.Register;

public sealed record RegisterCommand(string Username, string Email, string Password) : IRequest<Result>;
