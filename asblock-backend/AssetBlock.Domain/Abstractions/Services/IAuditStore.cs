using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Append-only audit persistence. No update/delete operations.</summary>
public interface IAuditStore
{
    Task Add(AuditLog entry, CancellationToken cancellationToken = default);
    Task<PagedResult<AuditLogListItem>> GetPaged(GetAuditLogsRequest request, CancellationToken cancellationToken = default);
}
