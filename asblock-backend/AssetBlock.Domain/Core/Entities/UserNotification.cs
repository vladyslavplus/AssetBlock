using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class UserNotification : BaseEntity
{
    public required Guid RecipientUserId { get; set; }
    public required NotificationKind Kind { get; set; }

    /// <summary>JSON payload (shape depends on <see cref="Kind" />).</summary>
    public required string MetadataJson { get; set; }

    public DateTimeOffset? ReadAt { get; set; }

    public User Recipient { get; set; } = null!;
}
