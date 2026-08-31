using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Primitives.Api;

namespace AssetBlock.Application.UseCases.Auth.Register;

public sealed record RegisterCommand(string Username, string Email, string Password) : IRequest<Result<TokensResponse>>;
