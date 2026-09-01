using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;

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

    /// <summary>Extends an unexpired claim owned by the supplied lock token.</summary>
    Task<bool> RenewLease(
        Guid id,
        Guid lockToken,
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

    /// <summary>
    /// Permanently transitions an outbox message to dead-lettered state upon exceeding max attempts or missing handler.
    /// Returns false when the lease was lost to another worker.
    /// </summary>
    Task<bool> MarkDeadLettered(
        Guid id,
        Guid lockToken,
        string reason,
        CancellationToken cancellationToken = default);

    /// <summary>Gets a paged list of dead-lettered outbox messages without serialized payloads.</summary>
    Task<PagedResult<DeadLetterOutboxListItemDto>> GetDeadLetters(
        GetDeadLettersRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Replays a dead-lettered message by resetting its status to PENDING and incrementing ReplayCount.
    /// Returns OutboxReplayOutcome indicating success, not found, or not dead-lettered.
    /// </summary>
    Task<(OutboxReplayOutcome Outcome, ReplayDeadLetterResponseDto? Response)> ReplayDeadLetter(
        Guid id,
        CancellationToken cancellationToken = default);
}
