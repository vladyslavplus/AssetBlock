using AssetBlock.Domain.Core.Primitives.Storage;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAssetStorageService
{
    Task EnsureBucket(CancellationToken cancellationToken = default);
    Task Upload(string key, Stream content, long objectSize, CancellationToken cancellationToken = default);
    Task OpenRead(string key, Func<Stream, CancellationToken, Task> consumer, CancellationToken cancellationToken = default);
    Task Delete(string key, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<StorageObjectInfo>> ListObjects(string? prefix = null, CancellationToken cancellationToken = default);
}
