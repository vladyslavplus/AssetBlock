using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Append-only audit journal row. Does not inherit mutable BaseEntity; no update/delete in app contracts.
/// </summary>
public sealed class AuditLog
{
    public long Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public AuditActorType ActorType { get; set; }
    public Guid? ActorUserId { get; set; }
    public required string Action { get; set; }
    public AuditOutcome Outcome { get; set; }
    public required string ResourceType { get; set; }
    public string? ResourceId { get; set; }
    public string? TraceId { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public string? MetadataJson { get; set; }
}
