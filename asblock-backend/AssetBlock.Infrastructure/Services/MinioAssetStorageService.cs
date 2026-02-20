using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;
using Polly.Registry;

namespace AssetBlock.Infrastructure.Services;

internal sealed class MinioAssetStorageService(
    IOptions<MinioOptions> options,
    ResiliencePipelineProvider<string> resilience,
    ILogger<MinioAssetStorageService> logger) : IAssetStorageService
{
    public async Task EnsureBucket(CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var client = new MinioClient()
            .WithEndpoint(opts.Endpoint)
            .WithCredentials(opts.AccessKey, opts.SecretKey)
            .WithSSL(opts.UseSsl)
            .Build();

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

    public async Task Upload(string key, Stream content, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var client = new MinioClient()
            .WithEndpoint(opts.Endpoint)
            .WithCredentials(opts.AccessKey, opts.SecretKey)
            .WithSSL(opts.UseSsl)
            .Build();

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
        var objectSize = content.CanSeek ? content.Length : -1;
        var pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.MINIO);
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

    public async Task<Stream> Get(string key, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var client = new MinioClient()
            .WithEndpoint(opts.Endpoint)
            .WithCredentials(opts.AccessKey, opts.SecretKey)
            .WithSSL(opts.UseSsl)
            .Build();

        var ms = new MemoryStream();
        var pipeline = resilience.GetPipeline(ResilienceConstants.Pipelines.MINIO);
        await pipeline.ExecuteAsync(async ct =>
            await client.GetObjectAsync(
                new GetObjectArgs()
                    .WithBucket(opts.Bucket)
                    .WithObject(key)
                    .WithCallbackStream(async (stream, token) => await stream.CopyToAsync(ms, token).ConfigureAwait(false)),
                ct).ConfigureAwait(false),
            cancellationToken);
        ms.Position = 0;
        return ms;
    }

    public async Task Delete(string key, CancellationToken cancellationToken = default)
    {
        var opts = options.Value;
        var client = new MinioClient()
            .WithEndpoint(opts.Endpoint)
            .WithCredentials(opts.AccessKey, opts.SecretKey)
            .WithSSL(opts.UseSsl)
            .Build();

        try
        {
            await client.RemoveObjectAsync(
                new RemoveObjectArgs().WithBucket(opts.Bucket).WithObject(key),
                cancellationToken).ConfigureAwait(false);
            logger.LogDebug("Deleted object {Key} from bucket {Bucket}", key, opts.Bucket);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "MinIO delete failed for key {Key}", key);
        }
    }
}
