using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Audit;

/// <summary>Admin audit log list filters. Client sort is not accepted; store always uses OccurredAt DESC, Id DESC.</summary>
public sealed record GetAuditLogsRequest : PagedRequest
{
    public Guid? ActorUserId { get; init; }
    public AuditActorType? ActorType { get; init; }
    public string? Action { get; init; }
    public AuditOutcome? Outcome { get; init; }
    public string? ResourceType { get; init; }
    public string? ResourceId { get; init; }
    public DateTimeOffset? From { get; init; }
    public DateTimeOffset? To { get; init; }

    /// <summary>Sort is fixed server-side; ignore client SortBy/SortDirection.</summary>
    public override SortDirection SortDirection { get; init; } = SortDirection.DESC;
}
