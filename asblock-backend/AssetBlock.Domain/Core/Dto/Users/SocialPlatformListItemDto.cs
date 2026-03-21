namespace AssetBlock.Domain.Core.Dto.Users;

public record SocialPlatformListItemDto
{
    public required Guid Id { get; init; }
    public required string Name { get; init; }
    public required string IconName { get; init; }
}
