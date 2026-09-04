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
    ILogger<S3CompatibleAssetStorageService> logger)
    : IAssetStorageService
{
    private readonly S3CompatibleObjectStore _store = new(client, bucket, resilience, logger);

    public Task EnsureBucket(CancellationToken cancellationToken = default) =>
        _store.EnsureBucket(cancellationToken);

    public Task Upload(string key, Stream content, long objectSize, CancellationToken cancellationToken = default) =>
        _store.Upload(key, content, objectSize, cancellationToken);

    public Task OpenRead(string key, Func<Stream, CancellationToken, Task> consumer, CancellationToken cancellationToken = default) =>
        _store.OpenRead(key, consumer, cancellationToken);

    public Task Delete(string key, CancellationToken cancellationToken = default) =>
        _store.Delete(key, cancellationToken);

    public IAsyncEnumerable<StorageObjectInfo> ListObjects(string? prefix = null, CancellationToken cancellationToken = default) =>
        _store.ListObjects(prefix, cancellationToken);
}
