using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>Transactional outbox row for reliable side-effect dispatch after DB commits.</summary>
public sealed class OutboxMessage
{
    public required Guid Id { get; init; }
    public required string Type { get; set; }
    public required string Payload { get; set; }
    public DateTimeOffset OccurredAt { get; set; } = DateTimeOffset.UtcNow;
    public OutboxMessageStatus Status { get; set; } = OutboxMessageStatus.PENDING;
    public int AttemptCount { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    /// <summary>Opaque lease token written on claim; mark operations must present the same value.</summary>
    public Guid? LockToken { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public DateTimeOffset? DeadLetteredAt { get; set; }
    public string? DeadLetterReason { get; set; }
    public int ReplayCount { get; set; }
    public DateTimeOffset? LastReplayedAt { get; set; }
    public string? LastError { get; set; }
}
