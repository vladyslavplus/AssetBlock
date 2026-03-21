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
    public string? AvatarUrl { get; init; }
    public string? Bio { get; init; }
    public required bool IsPublicProfile { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required List<UserSocialLinkDto> SocialLinks { get; init; }
}
