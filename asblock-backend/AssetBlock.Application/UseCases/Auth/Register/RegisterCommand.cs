using AssetBlock.Domain.Primitives.Api;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.Register;

public sealed record RegisterCommand(string Email, string Password) : IRequest<Result<TokensResponse>>;
