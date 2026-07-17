using AssetBlock.Domain.Core.Enums;
using System.Text.Json;

namespace AssetBlock.Domain.Core.Dto.Audit;

/// <summary>Admin API list item; metadata is a parsed JSON object (or null), never a double-encoded string.</summary>
public sealed record AuditLogListItem(
    long Id,
    DateTimeOffset OccurredAt,
    AuditActorType ActorType,
    Guid? ActorUserId,
    string Action,
    AuditOutcome Outcome,
    string ResourceType,
    string? ResourceId,
    string? TraceId,
    string? IpAddress,
    string? UserAgent,
    JsonElement? Metadata);
