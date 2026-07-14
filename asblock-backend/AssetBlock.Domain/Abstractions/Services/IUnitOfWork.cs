namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Coordinates a short EF Core database transaction across stores and outbox writes.</summary>
public interface IUnitOfWork
{
    /// <summary>
    /// Runs <paramref name="action"/> inside a database transaction and commits on success.
    /// Keep the action free of Stripe/MinIO/cache/SignalR I/O.
    /// </summary>
    Task ExecuteInTransaction(Func<CancellationToken, Task> action, CancellationToken cancellationToken = default);
}
