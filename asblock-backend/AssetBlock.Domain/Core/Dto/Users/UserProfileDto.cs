namespace AssetBlock.Domain.Core.Dto.Users;

public record UserSocialLinkDto
{
    public required Guid Id { get; init; }
    public required string PlatformName { get; init; }
    public required string IconName { get; init; }
    public required string Url { get; init; }
}

public record UserProfileDto
{
    public required Guid Id { get; init; }
    public required string Username { get; init; }
    /// <summary>Login email. Only populated for the authenticated user own profile (GET /me or owner viewing self); null on public profiles.</summary>
    public string? Email { get; init; }
    /// <summary>When the owner's email was verified. Null means verification pending. Own profile only.</summary>
    public DateTimeOffset? EmailVerifiedAt { get; init; }
    /// <summary>Pending email-change target for the owner only.</summary>
    public string? PendingEmail { get; init; }
    /// <summary>When the pending email-change link expires. Own profile only.</summary>
    public DateTimeOffset? PendingEmailChangeExpiresAt { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Bio { get; init; }
    public required bool IsPublicProfile { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required List<UserSocialLinkDto> SocialLinks { get; init; }

    /// <summary>Application role (e.g. Admin, User). Only when the caller views their own profile; otherwise null.</summary>
    public string? Role { get; init; }
}
