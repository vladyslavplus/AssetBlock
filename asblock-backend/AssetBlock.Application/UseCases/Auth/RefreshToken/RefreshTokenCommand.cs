using AssetBlock.Domain.Primitives.Api;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.RefreshToken;

public sealed record RefreshTokenCommand(string RefreshToken) : IRequest<Result<TokensResponse>>;
