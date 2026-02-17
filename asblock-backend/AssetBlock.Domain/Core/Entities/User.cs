using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class User : BaseEntity
{
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }

    public ICollection<Asset> AuthoredAssets { get; set; } = new List<Asset>();
    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
}
