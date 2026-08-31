using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Primitives.Api;

namespace AssetBlock.Application.UseCases.Auth.Login;

public sealed record LoginCommand(string Email, string Password) : IRequest<Result<TokensResponse>>;
