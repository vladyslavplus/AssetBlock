using Ardalis.Result;
using AssetBlock.Domain.Core.Primitives.Api;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.RefreshToken;

public sealed record RefreshTokenCommand(string RefreshToken) : IRequest<Result<TokensResponse>>;
