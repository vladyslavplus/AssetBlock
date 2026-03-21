using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class UserSocialLink : BaseEntity
{
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid PlatformId { get; set; }
    public SocialPlatform Platform { get; set; } = null!;

    public required string Url { get; set; }
}
