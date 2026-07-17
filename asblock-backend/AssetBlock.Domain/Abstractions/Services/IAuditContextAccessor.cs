using AssetBlock.Domain.Core.Dto.Audit;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Provides current HTTP/request audit context, or null outside an HTTP request (background jobs).</summary>
public interface IAuditContextAccessor
{
    CurrentAuditContext? GetCurrent();
}
