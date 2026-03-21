using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.Domain.Core.Dto.Notifications;

public sealed record GetNotificationsRequest : PagedRequest
{
    /// <summary>Default newest-first for notification feeds.</summary>
    public new SortDirection SortDirection { get; init; } = SortDirection.DESC;

    /// <summary>When true, only notifications with ReadAt unset.</summary>
    public bool? UnreadOnly { get; init; }

    public static readonly IReadOnlySet<string> AllowedSortBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CreatedAt"
    };
}
