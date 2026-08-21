using AssetBlock.Domain.Core.Primitives.Storage;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel.Args;
using Minio.Exceptions;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Services;

/// <summary>
/// Shared S3-compatible object operations via the MinIO .NET SDK.
/// EnsureBucket propagates failures except concurrent "already exists" races;
/// callers that want best-effort startup must catch.
/// </summary>
internal sealed class S3CompatibleObjectStore(
    IMinioClient client,
    string bucket,
    ResiliencePipelineProvider<string> resilience,
    ILogger logger)
{
    public async Task EnsureBucket(CancellationToken cancellationToken = default)
    {
        var pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STORAGE_REPLAYABLE);
        await pipeline.ExecuteAsync(async ct =>
        {
            var exists = await client.BucketExistsAsync(
                new BucketExistsArgs().WithBucket(bucket),
                ct).ConfigureAwait(false);
            if (exists)
            {
                return;
            }

            try
            {
                await client.MakeBucketAsync(
                    new MakeBucketArgs().WithBucket(bucket),
                    ct).ConfigureAwait(false);
                logger.LogInformation("Storage bucket {Bucket} created.", bucket);
            }
            catch (Exception ex) when (IsBucketAlreadyExists(ex))
            {
                // Concurrent EnsureBucket: another caller created the bucket first.
                logger.LogDebug(ex, "Storage bucket {Bucket} already exists (concurrent create).", bucket);
            }
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task Upload(string key, Stream content, long objectSize, CancellationToken cancellationToken = default)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STORAGE_STREAMING);
        await pipeline.ExecuteAsync(async ct =>
            await client.PutObjectAsync(
                new PutObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(key)
                    .WithStreamData(content)
                    .WithObjectSize(objectSize),
                ct).ConfigureAwait(false),
            cancellationToken);

        logger.LogDebug("Uploaded object {Key} to bucket {Bucket}", key, bucket);
    }

    public async Task OpenRead(string key, Func<Stream, CancellationToken, Task> consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        var pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STORAGE_STREAMING);
        await pipeline.ExecuteAsync(async ct =>
            await client.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(key)
                    .WithCallbackStream(async (stream, token) => await consumer(stream, token).ConfigureAwait(false)),
                ct).ConfigureAwait(false),
            cancellationToken);
    }

    public async Task Delete(string key, CancellationToken cancellationToken = default)
    {
        var pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STORAGE_REPLAYABLE);
        await pipeline.ExecuteAsync(async ct =>
            await client.RemoveObjectAsync(
                new RemoveObjectArgs().WithBucket(bucket).WithObject(key),
                ct).ConfigureAwait(false),
            cancellationToken);

        logger.LogDebug("Deleted object {Key} from bucket {Bucket}", key, bucket);
    }

    public async Task<IReadOnlyList<StorageObjectInfo>> ListObjects(string? prefix = null, CancellationToken cancellationToken = default)
    {
        var results = new List<StorageObjectInfo>();
        var listArgs = new ListObjectsArgs()
            .WithBucket(bucket)
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
            if (!string.IsNullOrEmpty(item.LastModified)
                && DateTimeOffset.TryParse(item.LastModified, out var parsed))
            {
                lastModified = parsed;
            }

            results.Add(new StorageObjectInfo(item.Key, lastModified, (long)item.Size));
        }

        return results;
    }

    private static bool IsBucketAlreadyExists(Exception ex)
    {
        if (ex is ErrorResponseException errorResponse)
        {
            var code = errorResponse.Response?.Code;
            if (string.Equals(code, "BucketAlreadyOwnedByYou", StringComparison.OrdinalIgnoreCase)
                || string.Equals(code, "BucketAlreadyExists", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // MinIO .NET 7 may surface BucketAlreadyOwnedByYou as ArgumentException("Bucket already owned by you: …").
        var message = ex.Message;
        return message.Contains("BucketAlreadyOwnedByYou", StringComparison.OrdinalIgnoreCase)
            || message.Contains("BucketAlreadyExists", StringComparison.OrdinalIgnoreCase)
            || message.Contains("already owned by you", StringComparison.OrdinalIgnoreCase);
    }
}
