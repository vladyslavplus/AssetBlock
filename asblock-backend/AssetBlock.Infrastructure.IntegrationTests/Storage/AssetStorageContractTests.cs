using System.Security.Cryptography;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using Polly.Registry;

namespace AssetBlock.Infrastructure.IntegrationTests.Storage;

/// <summary>Starts one provider container for the lifetime of a test class.</summary>
public abstract class StorageProviderFixture : IAsyncLifetime
{
    private static readonly TimeSpan _startTimeout = TimeSpan.FromMinutes(3);

    private IContainer? _container;

    protected abstract string Image { get; }
    protected abstract int ContainerPort { get; }
    protected abstract IWaitForContainerOS WaitStrategy { get; }
    protected abstract IReadOnlyDictionary<string, string> ContainerEnvironment { get; }
    protected abstract string[] Command { get; }
    protected abstract string AccessKey { get; }
    protected abstract string SecretKey { get; }
    protected abstract string Bucket { get; }
    protected abstract IAssetStorageService CreateStorage(string endpoint, string accessKey, string secretKey, string bucket);

    public IAssetStorageService Storage { get; private set; } = null!;
    private string Endpoint { get; set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder(Image)
            .WithPortBinding(ContainerPort, true)
            .WithEnvironment(ContainerEnvironment)
            .WithCommand(Command)
            .WithWaitStrategy(WaitStrategy)
            .Build();

        using var cts = new CancellationTokenSource(_startTimeout);
        try
        {
            await _container.StartAsync(cts.Token);
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                $"{Image} Testcontainers failed to start within {_startTimeout.TotalSeconds:0}s. " +
                "Check Docker Desktop is running.");
        }

        Endpoint = $"http://{_container.Hostname}:{_container.GetMappedPublicPort(ContainerPort)}";
        Storage = CreateStorage(Endpoint, AccessKey, SecretKey, Bucket);

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(90);
        Exception? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                await Storage.EnsureBucket(cts.Token);
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                await Task.Delay(500, cts.Token);
            }
        }

        if (last is not null)
        {
            throw new InvalidOperationException("Storage EnsureBucket did not become ready.", last);
        }
    }

    public IAssetStorageService CreateStorageForBucket(string bucket) =>
        CreateStorage(Endpoint, AccessKey, SecretKey, bucket);

    public async Task DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    protected static ResiliencePipelineProvider<string> CreateResilienceProvider()
    {
        var services = new ServiceCollection();
        services.AddResiliencePipeline(ResilienceConstants.Pipelines.STORAGE_REPLAYABLE, builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(120));
        });
        services.AddResiliencePipeline(ResilienceConstants.Pipelines.STORAGE_STREAMING, builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(120));
        });
        return services.BuildServiceProvider().GetRequiredService<ResiliencePipelineProvider<string>>();
    }
}

public sealed class MinioStorageFixture : StorageProviderFixture
{
    protected override string Image => "minio/minio:RELEASE.2025-09-07T16-13-09Z";
    protected override int ContainerPort => 9000;
    protected override string AccessKey => "assetblock";
    protected override string SecretKey => "dev_minio_secret";
    protected override string Bucket => "assets";

    protected override IWaitForContainerOS WaitStrategy =>
        Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
            r.ForPort(9000).ForPath("/minio/health/live"));

    protected override IReadOnlyDictionary<string, string> ContainerEnvironment { get; } =
        new Dictionary<string, string>
        {
            ["MINIO_ROOT_USER"] = "assetblock",
            ["MINIO_ROOT_PASSWORD"] = "dev_minio_secret"
        };

    protected override string[] Command { get; } =
        ["server", "/data", "--console-address", ":9001"];

    protected override IAssetStorageService CreateStorage(string endpoint, string accessKey, string secretKey, string bucket)
    {
        var client = S3CompatibleClientFactory.Create(endpoint, accessKey, secretKey, useSsl: false);
        var opts = Microsoft.Extensions.Options.Options.Create(new MinioOptions
        {
            Endpoint = endpoint,
            Bucket = bucket,
            AccessKey = accessKey,
            SecretKey = secretKey,
            UseSsl = false
        });
        return new MinioAssetStorageService(
            client,
            opts,
            CreateResilienceProvider(),
            NullLogger<MinioAssetStorageService>.Instance);
    }
}

public sealed class SeaweedFsStorageFixture : StorageProviderFixture
{
    protected override string Image => "chrislusf/seaweedfs:4.42";
    protected override int ContainerPort => 8333;
    protected override string AccessKey => "assetblock";
    protected override string SecretKey => "dev_seaweedfs_secret";
    protected override string Bucket => "assets";

    // S3 gateway often answers unsigned GETs with 403; treat any HTTP response as ready.
    protected override IWaitForContainerOS WaitStrategy =>
        Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r =>
            r.ForPort(8333)
                .ForPath("/")
                .ForStatusCodeMatching(_ => true));

    protected override IReadOnlyDictionary<string, string> ContainerEnvironment { get; } =
        new Dictionary<string, string>
        {
            ["AWS_ACCESS_KEY_ID"] = "assetblock",
            ["AWS_SECRET_ACCESS_KEY"] = "dev_seaweedfs_secret",
            ["S3_BUCKET"] = "assets"
        };

    protected override string[] Command { get; } =
        ["mini", "-ip=0.0.0.0", "-ip.bind=0.0.0.0", "-dir=/data", "-s3.port=8333"];

    protected override IAssetStorageService CreateStorage(string endpoint, string accessKey, string secretKey, string bucket)
    {
        var client = S3CompatibleClientFactory.Create(endpoint, accessKey, secretKey, useSsl: false);
        var opts = Microsoft.Extensions.Options.Options.Create(new SeaweedFsOptions
        {
            Endpoint = endpoint,
            Bucket = bucket,
            AccessKey = accessKey,
            SecretKey = secretKey,
            UseSsl = false
        });
        return new SeaweedFsAssetStorageService(
            client,
            opts,
            CreateResilienceProvider(),
            NullLogger<SeaweedFsAssetStorageService>.Instance);
    }
}

/// <summary>Shared IAssetStorageService contract against real S3-compatible providers.</summary>
public abstract class AssetStorageContractTests(StorageProviderFixture fixture)
{
    private readonly string _keyPrefix = $"contract/{Guid.NewGuid():N}/";

    private IAssetStorageService Storage => fixture.Storage;

    private string Key(string suffix) => _keyPrefix + suffix;

    private IAssetStorageService CreateStorageForBucket(string bucket) =>
        fixture.CreateStorageForBucket(bucket);

    private static async Task<List<Domain.Core.Primitives.Storage.StorageObjectInfo>> ToList(
        IAsyncEnumerable<Domain.Core.Primitives.Storage.StorageObjectInfo> source,
        CancellationToken cancellationToken = default)
    {
        var list = new List<Domain.Core.Primitives.Storage.StorageObjectInfo>();
        await foreach (var item in source.WithCancellation(cancellationToken))
        {
            list.Add(item);
        }
        return list;
    }

    [Fact]
    public async Task EnsureBucket_WhenCalledRepeatedly_ShouldBeIdempotent()
    {
        await Storage.EnsureBucket();
        await Storage.EnsureBucket();
    }

    [Fact]
    public async Task EnsureBucket_WhenCalledConcurrentlyOnMissingBucket_ShouldBeSafe()
    {
        var bucket = $"race{Guid.NewGuid():N}";
        var storages = Enumerable.Range(0, 8)
            .Select(_ => CreateStorageForBucket(bucket))
            .ToArray();

        var tasks = storages.Select(s => s.EnsureBucket()).ToArray();
        await Task.WhenAll(tasks);

        // Post-condition: bucket is usable for a tiny object.
        var key = Key("after-race.bin");
        var payload = "ok"u8.ToArray();
        await storages[0].Upload(key, new MemoryStream(payload), payload.Length);
        var listed = await ToList(storages[0].ListObjects(_keyPrefix));
        listed.Should().Contain(o => o.Key == key);
    }

    [Fact]
    public async Task UploadAndOpenRead_WhenNonSeekableStream_ShouldRoundTripBytes()
    {
        var key = Key("roundtrip.bin");
        var payload = RandomNumberGenerator.GetBytes(64 * 1024);
        await using var nonSeekable = new NonSeekableStream(new MemoryStream(payload));
        await Storage.Upload(key, nonSeekable, payload.Length);

        byte[]? read = null;
        await Storage.OpenRead(key, async (stream, ct) =>
        {
            await using var ms = new MemoryStream();
            await stream.CopyToAsync(ms, ct);
            read = ms.ToArray();
        });

        read.Should().Equal(payload);

        var listed = await ToList(Storage.ListObjects(_keyPrefix));
        var info = listed.Should().ContainSingle(o => o.Key == key).Subject;
        info.Size.Should().Be(payload.Length);
        info.LastModified.Should().NotBeNull();
    }

    [Fact]
    public async Task ListObjects_WhenNestedPrefix_ShouldReturnKeySizeAndTimestamp()
    {
        var key = Key("nested/a/b/c.dat");
        var payload = "nested-payload"u8.ToArray();
        await Storage.Upload(key, new MemoryStream(payload), payload.Length);

        var listed = await ToList(Storage.ListObjects(_keyPrefix + "nested/"));
        var info = listed.Should().ContainSingle(o => o.Key == key).Subject;
        info.Size.Should().Be(payload.Length);
        info.LastModified.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_WhenRepeated_ShouldBeIdempotent()
    {
        var key = Key("delete-me.bin");
        var payload = "x"u8.ToArray();
        await Storage.Upload(key, new MemoryStream(payload), payload.Length);
        await Storage.Delete(key);
        await Storage.Delete(key);

        var listed = await ToList(Storage.ListObjects(_keyPrefix));
        listed.Should().NotContain(o => o.Key == key);
    }

    [Fact]
    public async Task Operations_WhenPreCancelled_ShouldPropagateCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await FluentActions.Awaiting(() => Storage.EnsureBucket(cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => Storage.Upload(Key("c.bin"), new MemoryStream([1]), 1, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => Storage.OpenRead(Key("missing"), (_, _) => Task.CompletedTask, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => Storage.Delete(Key("missing"), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        await FluentActions.Awaiting(() => ToList(Storage.ListObjects(_keyPrefix, cts.Token), cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Upload_WhenSourceThrowsMidStream_ShouldNotLeaveVisiblePartialObject()
    {
        var key = Key("partial-throw.bin");
        await using var throwing = new ThrowingAfterBytesStream(byteCountBeforeThrow: 32 * 1024);

        await FluentActions.Awaiting(() => Storage.Upload(key, throwing, objectSize: 8 * 1024 * 1024))
            .Should().ThrowAsync<Exception>();

        var listed = await ToList(Storage.ListObjects(_keyPrefix));
        listed.Should().NotContain(o => o.Key == key);
    }

    [Fact]
    public async Task Upload_WhenCancelledMidStream_ShouldNotLeaveVisiblePartialObject()
    {
        var key = Key("partial-cancel.bin");
        using var cts = new CancellationTokenSource();
        await using var slow = new SlowByteStream(totalBytes: 8 * 1024 * 1024, cancelAfterBytes: 64 * 1024, cts);

        await FluentActions.Awaiting(() => Storage.Upload(key, slow, objectSize: 8 * 1024 * 1024, cts.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        // Verification must not reuse the cancelled token from the aborted upload.
        var listed = await ToList(Storage.ListObjects(_keyPrefix));
        listed.Should().NotContain(o => o.Key == key);
    }

    [Fact]
    public async Task UploadAndOpenRead_WhenLargeObject_ShouldMatchLengthAndSha256()
    {
        var key = Key("large.bin");
        const int size = 12 * 1024 * 1024;
        await using var source = new DeterministicByteStream(size);
        var expectedHash = source.ComputeSha256Hex();
        source.Reset();

        await Storage.Upload(key, source, size);

        long length = 0;
        string? actualHash = null;
        await Storage.OpenRead(key, async (stream, ct) =>
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct)) > 0)
            {
                sha.AppendData(buffer.AsSpan(0, read));
                length += read;
            }

            actualHash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        });

        length.Should().Be(size);
        actualHash.Should().Be(expectedHash);

        var listed = await ToList(Storage.ListObjects(_keyPrefix));
        listed.Should().Contain(o => o.Key == key && o.Size == size && o.LastModified != null);
    }

    private sealed class NonSeekableStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingAfterBytesStream(int byteCountBeforeThrow) : Stream
    {
        private int _remaining = byteCountBeforeThrow;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_remaining <= 0)
            {
                throw new IOException("Simulated mid-stream source failure.");
            }

            var n = Math.Min(count, _remaining);
            Array.Fill(buffer, (byte)0xAB, offset, n);
            _remaining -= n;
            return n;
        }
    }

    private sealed class SlowByteStream(int totalBytes, int cancelAfterBytes, CancellationTokenSource cts) : Stream
    {
        private int _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_read >= cancelAfterBytes)
            {
                await cts.CancelAsync();
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (_read >= totalBytes)
            {
                return 0;
            }

            var n = Math.Min(count, totalBytes - _read);
            Array.Fill(buffer, (byte)0xCD, offset, n);
            _read += n;
            await Task.Delay(1, cancellationToken);
            return n;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }

    private sealed class DeterministicByteStream(int totalBytes) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public void Reset() => _position = 0;

        public string ComputeSha256Hex()
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            var remaining = totalBytes;
            var pos = 0;
            while (remaining > 0)
            {
                var n = Math.Min(buffer.Length, remaining);
                Fill(buffer.AsSpan(0, n), pos);
                sha.AppendData(buffer.AsSpan(0, n));
                pos += n;
                remaining -= n;
            }

            return Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= totalBytes)
            {
                return 0;
            }

            var n = Math.Min(count, totalBytes - _position);
            Fill(buffer.AsSpan(offset, n), _position);
            _position += n;
            return n;
        }

        private static void Fill(Span<byte> destination, int start)
        {
            for (var i = 0; i < destination.Length; i++)
            {
                destination[i] = (byte)((start + i) & 0xFF);
            }
        }
    }
}

public sealed class MinioAssetStorageContractTests(MinioStorageFixture fixture)
    : AssetStorageContractTests(fixture), IClassFixture<MinioStorageFixture>;

public sealed class SeaweedFsAssetStorageContractTests(SeaweedFsStorageFixture fixture)
    : AssetStorageContractTests(fixture), IClassFixture<SeaweedFsStorageFixture>;
