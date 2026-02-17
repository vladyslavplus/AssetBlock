namespace AssetBlock.Domain.Entities;

/// <summary>
/// Stored refresh token (hash) for JWT refresh flow.
/// </summary>
public class RefreshToken
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; set; }
    /// <summary>Hash of the refresh token (never store plain token).</summary>
    public required string TokenHash { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }

    public User User { get; set; } = null!;
}
