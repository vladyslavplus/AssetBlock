using AssetBlock.Domain.Core.Primitives.Storage;
using Microsoft.Extensions.Logging;
using Minio;
using Minio.DataModel;
using Minio.DataModel.Args;
using Minio.DataModel.Response;
using Minio.Exceptions;
using Polly;
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
    ILogger logger,
    Uri? versionListEndpoint = null,
    string? accessKey = null,
    string? secretKey = null)
{
    private readonly S3VersionListingClient? _versionListing = versionListEndpoint is not null
        && !string.IsNullOrWhiteSpace(accessKey) && !string.IsNullOrWhiteSpace(secretKey)
            ? new S3VersionListingClient(versionListEndpoint, bucket, accessKey, secretKey)
            : null;
    public async Task EnsureBucket(CancellationToken cancellationToken = default)
    {
        ResiliencePipeline pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STORAGE_REPLAYABLE);
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

    public async Task<string> UploadVersioned(string key, Stream content, long objectSize, CancellationToken cancellationToken = default)
    {
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        ResiliencePipeline pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STORAGE_STREAMING);
        PutObjectResponse? response = null;
        await pipeline.ExecuteAsync(async ct =>
            response = await client.PutObjectAsync(
                new PutObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(key)
                    .WithStreamData(content)
                    .WithObjectSize(objectSize)
                    .WithContentType("application/octet-stream"),
                ct).ConfigureAwait(false),
            cancellationToken);

        ObjectStat stat = await client.StatObjectAsync(
            new StatObjectArgs().WithBucket(bucket).WithObject(key),
            cancellationToken).ConfigureAwait(false);
        if (response is null || stat is null || !string.Equals(response.Etag?.Trim('"'), stat.ETag, StringComparison.Ordinal))
        {
            logger.LogError("Uploaded object {Key} could not be matched to provider stat response; retaining without generation", key);
            throw new InvalidOperationException("Uploaded object could not be matched to its provider stat response.");
        }

        if (!StorageVersionIdentity.IsImmutable(stat.VersionId))
        {
            logger.LogError("Storage provider returned no immutable generation for uploaded object {Key}; object retained", key);
            throw new InvalidOperationException("Storage bucket must return an immutable object version ID.");
        }

        logger.LogDebug("Uploaded object to bucket {Bucket}", bucket);
        return stat.VersionId;
    }

    public async Task OpenRead(string key, Func<Stream, CancellationToken, Task> consumer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(consumer);
        ResiliencePipeline pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STORAGE_STREAMING);
        await pipeline.ExecuteAsync(async ct =>
            await client.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(bucket)
                    .WithObject(key)
                    .WithCallbackStream(async (stream, token) => await consumer(stream, token).ConfigureAwait(false)),
                ct).ConfigureAwait(false),
            cancellationToken);
    }

    public async Task Delete(string key, string versionId, CancellationToken cancellationToken = default)
    {
        if (!StorageVersionIdentity.IsImmutable(versionId))
        {
            throw new InvalidOperationException("Refusing object deletion without an exact storage version ID.");
        }

        ResiliencePipeline pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STORAGE_REPLAYABLE);
        await pipeline.ExecuteAsync(async ct =>
            await client.RemoveObjectAsync(
                new RemoveObjectArgs().WithBucket(bucket).WithObject(key).WithVersionId(versionId),
                ct).ConfigureAwait(false),
            cancellationToken);

        logger.LogDebug("Deleted object from bucket {Bucket}", bucket);
    }

    public async IAsyncEnumerable<StorageObjectInfo> ListObjects(
        string? prefix = null,
        StorageObjectCursor? cursor = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ListObjectsArgs listArgs = new ListObjectsArgs()
            .WithBucket(bucket)
            .WithRecursive(true)
            .WithVersions(true);

        if (!string.IsNullOrEmpty(prefix))
        {
            listArgs = listArgs.WithPrefix(prefix);
        }

        var passedCursor = cursor is null;
        await foreach (Item? item in client.ListObjectsEnumAsync(listArgs, cancellationToken).ConfigureAwait(false))
        {
            if (item.IsDir)
            {
                continue;
            }

            if (!passedCursor)
            {
                passedCursor = string.Equals(item.Key, cursor!.Key, StringComparison.Ordinal)
                    && string.Equals(item.VersionId, cursor.VersionId, StringComparison.Ordinal);
                continue;
            }

            DateTimeOffset? lastModified = null;
            if (!string.IsNullOrEmpty(item.LastModified)
                && DateTimeOffset.TryParse(item.LastModified, out DateTimeOffset parsed))
            {
                lastModified = parsed;
            }

            yield return new StorageObjectInfo(item.Key, lastModified, (long)item.Size, item.VersionId);
        }
    }

    public Task<StorageObjectPage> ListObjectVersionsPage(
        string? prefix,
        StorageObjectCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (_versionListing is null)
        {
            throw new InvalidOperationException("Exact S3 version pagination is unavailable without provider endpoint credentials.");
        }

        ResiliencePipeline pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.STORAGE_REPLAYABLE);
        return pipeline.ExecuteAsync(
            async ct => await _versionListing.ListPage(prefix, cursor, pageSize, ct).ConfigureAwait(false),
            cancellationToken).AsTask();
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
