using System.Collections.Concurrent;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.Storage;

namespace AssetBlock.WebApi.IntegrationTests.Support.Fakes;

/// <summary>Test-only in-memory object store for WebApi DI; avoids a real MinIO dependency.</summary>
public sealed class FakeAssetStorageService : IAssetStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();

    public Task EnsureBucket(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task Upload(string key, Stream content, long objectSize, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        _objects[key] = buffer.ToArray();
    }

    public async Task OpenRead(string key, Func<Stream, CancellationToken, Task> consumer, CancellationToken cancellationToken = default)
    {
        if (!_objects.TryGetValue(key, out var bytes))
        {
            throw new FileNotFoundException($"Object not found in fake storage: {key}");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        await consumer(stream, cancellationToken).ConfigureAwait(false);
    }

    public Task Delete(string key, CancellationToken cancellationToken = default)
    {
        _objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<StorageObjectInfo> ListObjects(
        string? prefix = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var key in _objects.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return new StorageObjectInfo(key, DateTimeOffset.UtcNow, _objects[key].LongLength);
            }
        }

        await Task.CompletedTask;
    }

    /// <summary>Test helper: seeds a pre-encrypted object directly (bypassing Upload's stream copy).</summary>
    public void Seed(string key, byte[] content) => _objects[key] = content;

    public bool Contains(string key) => _objects.ContainsKey(key);
}
