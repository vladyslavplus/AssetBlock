using AssetBlock.Domain.Core.Primitives.Storage;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAssetStorageService
{
    Task EnsureBucket(CancellationToken cancellationToken = default);
    Task<string> UploadVersioned(string key, Stream content, long objectSize, CancellationToken cancellationToken = default);
    Task OpenRead(string key, Func<Stream, CancellationToken, Task> consumer, CancellationToken cancellationToken = default);
    Task Delete(string key, string versionId, CancellationToken cancellationToken = default);
    IAsyncEnumerable<StorageObjectInfo> ListObjects(string? prefix = null, CancellationToken cancellationToken = default);
    IAsyncEnumerable<StorageObjectInfo> ListObjects(
        string? prefix,
        StorageObjectCursor? cursor,
        CancellationToken cancellationToken = default);
    Task<StorageObjectPage> ListObjectVersionsPage(
        string? prefix,
        StorageObjectCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default);
}
