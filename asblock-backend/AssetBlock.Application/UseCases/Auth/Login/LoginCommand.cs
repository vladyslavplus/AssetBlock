using Ardalis.Result;
using AssetBlock.Domain.Core.Primitives.Api;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.Login;

public sealed record LoginCommand(string Email, string Password) : IRequest<Result<TokensResponse>>;
