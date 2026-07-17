using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Audit;

/// <summary>Resolved actor and HTTP request context for the current execution.</summary>
public sealed record CurrentAuditContext(
    AuditActorType ActorType,
    Guid? ActorUserId,
    string? TraceId,
    string? IpAddress,
    string? UserAgent);
