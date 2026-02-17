namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Stored refresh token (hash) for JWT refresh flow.
/// </summary>
public class RefreshToken
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; set; }
    /// <summary>Hash of the refresh token (never store plain token).</summary>
    public required string TokenHash { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? RevokedAt { get; set; }

    public User User { get; set; } = null!;
}
