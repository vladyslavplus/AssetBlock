using AssetBlock.Domain.Primitives.Api;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.Login;

public sealed record LoginCommand(string Email, string Password) : IRequest<Result<TokensResponse>>;
