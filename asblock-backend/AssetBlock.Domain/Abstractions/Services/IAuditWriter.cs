using AssetBlock.Domain.Core.Dto.Audit;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Writes audit events. Write is mandatory (throws on failure for transactional success paths).
/// WriteBestEffort logs and swallows infrastructure failures except OperationCanceledException.
/// </summary>
public interface IAuditWriter
{
    Task Write(AuditEvent auditEvent, CancellationToken cancellationToken = default);
    Task WriteBestEffort(AuditEvent auditEvent, CancellationToken cancellationToken = default);
}
