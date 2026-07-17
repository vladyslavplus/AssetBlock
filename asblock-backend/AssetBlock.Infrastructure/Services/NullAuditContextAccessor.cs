using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Audit;

namespace AssetBlock.Infrastructure.Services;

/// <summary>Default accessor when no HTTP context is available (background jobs / design-time).</summary>
internal sealed class NullAuditContextAccessor : IAuditContextAccessor
{
    public CurrentAuditContext? GetCurrent() => null;
}
