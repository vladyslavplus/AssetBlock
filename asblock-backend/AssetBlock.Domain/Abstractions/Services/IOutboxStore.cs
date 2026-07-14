using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IOutboxStore
{
    /// <summary>Stages an outbox message in the current DbContext (SaveChanges via store or unit of work).</summary>
    Task Enqueue(string type, object payload, CancellationToken cancellationToken = default);

    /// <summary>Claims a batch with FOR UPDATE SKIP LOCKED, sets a new LockToken + lease, increments AttemptCount.</summary>
    Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatch(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks processed only if Id + LockToken still match and ProcessedAt is null.
    /// Returns false when the lease was lost to another worker.
    /// </summary>
    Task<bool> MarkProcessed(Guid id, Guid lockToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records failure / next attempt only if Id + LockToken still match and ProcessedAt is null.
    /// Returns false when the lease was lost to another worker.
    /// </summary>
    Task<bool> MarkFailed(
        Guid id,
        Guid lockToken,
        string error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default);
}
