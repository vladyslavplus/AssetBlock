using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Auth.Logout;

public sealed record LogoutCommand(string RefreshToken) : IRequest<Result>;
