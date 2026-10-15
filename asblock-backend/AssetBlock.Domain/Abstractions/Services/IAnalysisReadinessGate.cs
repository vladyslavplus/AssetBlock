namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Fail-closed readiness for shared analysis claims. New CODE_ANALYSIS work is claimed only when
/// the configured capacity registration, the exact persisted pinned policy row and the registered
/// handler are all proven; occupied reservations are still reconciled while new claims stay shut.
/// </summary>
public interface IAnalysisReadinessGate
{
    Task<bool> EnsureReady(CancellationToken cancellationToken = default);
}
