using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class User : BaseEntity
{
    public required string Username { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
    public required string Role { get; set; } = AppRoles.USER;

    public ICollection<Asset> AuthoredAssets { get; set; } = new List<Asset>();
    public ICollection<Purchase> Purchases { get; set; } = new List<Purchase>();
    public ICollection<RefreshToken> RefreshTokens { get; set; } = new List<RefreshToken>();
    public ICollection<Review> Reviews { get; set; } = new List<Review>();
}
