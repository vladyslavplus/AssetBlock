using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Domain.Core.Primitives.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Services;

internal sealed class MinioAssetStorageService(
    IMinioClient client,
    IOptions<MinioOptions> options,
    ResiliencePipelineProvider<string> resilience,
    ILogger<MinioAssetStorageService> logger) : IAssetStorageService
{
    public async Task EnsureBucket(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;

        try
        {
            var exists = await client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(opts.Bucket),
                cancellationToken).ConfigureAwait(false);
            if (!exists)
            {
                await client.MakeBucketAsync(
                    new MakeBucketArgs().WithBucket(opts.Bucket),
                    cancellationToken).ConfigureAwait(false);
                logger.LogInformation("MinIO bucket {Bucket} created.", opts.Bucket);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not ensure MinIO bucket {Bucket}; uploads may still work if it already exists.", opts.Bucket);
        }
    }

    public async Task Upload(string key, Stream content, long objectSize, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;

        try
        {
            await client.MakeBucketAsync(
                new MakeBucketArgs().WithBucket(opts.Bucket),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Bucket may already exist or other transient error; continue with PutObject.
        }

        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.MINIO_STREAMING);
        await pipeline.ExecuteAsync(async ct =>
            await client.PutObjectAsync(
                new PutObjectArgs()
                    .WithBucket(opts.Bucket)
                    .WithObject(key)
                    .WithStreamData(content)
                    .WithObjectSize(objectSize),
                ct).ConfigureAwait(false),
            cancellationToken);

        logger.LogDebug("Uploaded object {Key} to bucket {Bucket}", key, opts.Bucket);
    }

    public async Task OpenRead(string key, Func<Stream, CancellationToken, Task> consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var opts = options.Value;
        var pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.MINIO_STREAMING);
        await pipeline.ExecuteAsync(async ct =>
            await client.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(opts.Bucket)
                    .WithObject(key)
                    .WithCallbackStream(async (stream, token) => await consumer(stream, token).ConfigureAwait(false)),
                ct).ConfigureAwait(false),
            cancellationToken);
    }

    public async Task Delete(string key, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;

        var pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.MINIO_REPLAYABLE);
        await pipeline.ExecuteAsync(async ct =>
            await client.RemoveObjectAsync(
                new RemoveObjectArgs().WithBucket(opts.Bucket).WithObject(key),
                ct).ConfigureAwait(false),
            cancellationToken);

        logger.LogDebug("Deleted object {Key} from bucket {Bucket}", key, opts.Bucket);
    }

    public async Task<IReadOnlyList<StorageObjectInfo>> ListObjects(string? prefix = null, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var results = new List<StorageObjectInfo>();
        var listArgs = new ListObjectsArgs()
            .WithBucket(opts.Bucket)
            .WithRecursive(true);

        if (!string.IsNullOrEmpty(prefix))
        {
            listArgs = listArgs.WithPrefix(prefix);
        }

        await foreach (var item in client.ListObjectsEnumAsync(listArgs, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsDir)
            {
                continue;
            }

            DateTimeOffset? lastModified = null;
            if (!string.IsNullOrEmpty(item.LastModified))
            {
                if (DateTimeOffset.TryParse(item.LastModified, out var parsed))
                {
                    lastModified = parsed;
                }
            }

            results.Add(new StorageObjectInfo(item.Key, lastModified, (long)item.Size));
        }

        return results;
    }
}
