namespace AssetBlock.Domain.Core.Dto.Auth;

public enum RefreshTokenValidationStatus
{
    VALID,
    REVOKED_REUSED,
    NOT_FOUND_OR_EXPIRED
}

public sealed record RefreshTokenValidationResult(
    RefreshTokenValidationStatus Status,
    Guid? UserId = null,
    string? Username = null,
    string? Email = null,
    string? Role = null,
    Guid? TokenId = null
);
