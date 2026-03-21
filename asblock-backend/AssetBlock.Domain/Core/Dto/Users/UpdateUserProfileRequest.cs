namespace AssetBlock.Domain.Core.Dto.Users;

public record UpdateUserProfileRequest
{
    public string? Username { get; init; }
    public string? AvatarUrl { get; init; }
    public string? Bio { get; init; }
    public bool? IsPublicProfile { get; init; }
}

public record UpdateUserSocialLinksRequest
{
    // A list of Platform IDs to URLs. Useful for fully replacing the user's social links.
    public required List<SocialLinkInput> Links { get; init; }
}

public record SocialLinkInput
{
    public required Guid PlatformId { get; init; }
    public required string Url { get; init; }
}
