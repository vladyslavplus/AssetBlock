using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Domain.Core.Primitives.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Services;

internal sealed class SeaweedFsAssetStorageService : IAssetStorageService
{
    private readonly S3CompatibleObjectStore _store;

    public SeaweedFsAssetStorageService(
        IMinioClient client,
        IOptions<SeaweedFsOptions> options,
        ResiliencePipelineProvider<string> resilience,
        ILogger<SeaweedFsAssetStorageService> logger)
    {
        var opts = options.Value;
        _store = new S3CompatibleObjectStore(client, opts.Bucket, resilience, logger);
    }

    public Task EnsureBucket(CancellationToken cancellationToken = default) =>
        _store.EnsureBucket(cancellationToken);

    public Task Upload(string key, Stream content, long objectSize, CancellationToken cancellationToken = default) =>
        _store.Upload(key, content, objectSize, cancellationToken);

    public Task OpenRead(string key, Func<Stream, CancellationToken, Task> consumer, CancellationToken cancellationToken = default) =>
        _store.OpenRead(key, consumer, cancellationToken);

    public Task Delete(string key, CancellationToken cancellationToken = default) =>
        _store.Delete(key, cancellationToken);

    public Task<IReadOnlyList<StorageObjectInfo>> ListObjects(string? prefix = null, CancellationToken cancellationToken = default) =>
        _store.ListObjects(prefix, cancellationToken);
}
