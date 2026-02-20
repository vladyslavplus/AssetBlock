namespace AssetBlock.Domain.Core.Dto.Auth;

public sealed record RegisterRequest(string Username, string Email, string Password);
