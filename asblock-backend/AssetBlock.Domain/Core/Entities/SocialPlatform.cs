using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class SocialPlatform : BaseEntity
{
    public required string Name { get; set; }
    public required string IconName { get; set; }
    
    public ICollection<UserSocialLink> UserLinks { get; set; } = new List<UserSocialLink>();
}
