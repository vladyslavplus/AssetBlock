using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Audit;

/// <summary>Immutable draft describing an audit event to persist.</summary>
public sealed record AuditEvent(
    string Action,
    AuditOutcome Outcome,
    string ResourceType,
    string? ResourceId = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    AuditActorType? ActorTypeOverride = null,
    Guid? ActorUserIdOverride = null);
