using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>One mutable current action state per (UserId, Purpose). Not append-only history.</summary>
public sealed class EmailAction
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public EmailActionPurpose Purpose { get; set; }
    public required string TargetEmail { get; set; }
    public Guid Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? ConsumedAt { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }

    public User User { get; set; } = null!;
}
