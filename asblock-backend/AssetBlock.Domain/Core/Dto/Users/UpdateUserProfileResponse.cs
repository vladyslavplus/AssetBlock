namespace AssetBlock.Domain.Core.Dto.Users;

/// <summary>
/// PATCH /me response: editable profile fields only (no id, timestamps, or social links).
/// </summary>
public record UpdateUserProfileResponse
{
    public required string Username { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Bio { get; init; }
    public required bool IsPublicProfile { get; init; }
}
