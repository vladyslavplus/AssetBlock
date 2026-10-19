using AssetBlock.Domain.Core.Primitives.Storage;

namespace AssetBlock.Domain.Core.Dto;

public sealed record StorageOrphanCleanupLease(Guid LeaseId, StorageObjectCursor? Cursor);
