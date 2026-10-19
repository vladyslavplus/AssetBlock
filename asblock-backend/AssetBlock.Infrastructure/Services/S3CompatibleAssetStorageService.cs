using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.Storage;
using Microsoft.Extensions.Logging;
using Minio;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Services;

internal sealed class S3CompatibleAssetStorageService(
    IMinioClient client,
    string bucket,
    ResiliencePipelineProvider<string> resilience,
    ILogger<S3CompatibleAssetStorageService> logger,
    Uri? versionListEndpoint = null,
    string? accessKey = null,
    string? secretKey = null)
    : IAssetStorageService
{
    private readonly S3CompatibleObjectStore _store = new(
        client, bucket, resilience, logger, versionListEndpoint, accessKey, secretKey);

    public Task EnsureBucket(CancellationToken cancellationToken = default) =>
        _store.EnsureBucket(cancellationToken);

    public Task<string> UploadVersioned(string key, Stream content, long objectSize, CancellationToken cancellationToken = default) =>
        _store.UploadVersioned(key, content, objectSize, cancellationToken);

    public Task OpenRead(string key, Func<Stream, CancellationToken, Task> consumer, CancellationToken cancellationToken = default) =>
        _store.OpenRead(key, consumer, cancellationToken);

    public Task Delete(string key, string versionId, CancellationToken cancellationToken = default) =>
        _store.Delete(key, versionId, cancellationToken);

    public IAsyncEnumerable<StorageObjectInfo> ListObjects(string? prefix = null, CancellationToken cancellationToken = default) =>
        _store.ListObjects(prefix, null, cancellationToken);

    public IAsyncEnumerable<StorageObjectInfo> ListObjects(
        string? prefix,
        StorageObjectCursor? cursor,
        CancellationToken cancellationToken = default) =>
        _store.ListObjects(prefix, cursor, cancellationToken);

    public Task<StorageObjectPage> ListObjectVersionsPage(
        string? prefix,
        StorageObjectCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        _store.ListObjectVersionsPage(prefix, cursor, pageSize, cancellationToken);
}
