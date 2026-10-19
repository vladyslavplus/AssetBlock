using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Primitives.Storage;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IStorageOrphanCleanupStateStore
{
    Task<StorageOrphanCleanupLease?> TryAcquire(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> SaveCursor(
        Guid leaseId,
        StorageObjectCursor? cursor,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task Release(Guid leaseId, bool resetCursor, CancellationToken cancellationToken = default);
}
