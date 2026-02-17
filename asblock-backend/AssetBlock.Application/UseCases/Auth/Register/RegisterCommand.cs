using Ardalis.Result;
using AssetBlock.Domain.Core.Primitives.Api;
using MediatR;

namespace AssetBlock.Application.UseCases.Auth.Register;

public sealed record RegisterCommand(string Email, string Password) : IRequest<Result<TokensResponse>>;
