using System.Collections.Concurrent;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.Storage;

namespace AssetBlock.WebApi.IntegrationTests.Support.Fakes;

/// <summary>Test-only in-memory object store for WebApi DI; avoids a real MinIO dependency.</summary>
public sealed class FakeAssetStorageService : IAssetStorageService
{
    private readonly ConcurrentDictionary<string, byte[]> _objects = new();
    private readonly ConcurrentDictionary<string, string> _versions = new();
    private int _openReadCount;

    public int OpenReadCount => Volatile.Read(ref _openReadCount);

    public Task EnsureBucket(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public async Task<string> UploadVersioned(string key, Stream content, long objectSize, CancellationToken cancellationToken = default)
    {
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
        _objects[key] = buffer.ToArray();
        var versionId = Guid.NewGuid().ToString("N");
        _versions[key] = versionId;
        return versionId;
    }

    public async Task OpenRead(string key, Func<Stream, CancellationToken, Task> consumer, CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _openReadCount);
        if (!_objects.TryGetValue(key, out var bytes))
        {
            throw new FileNotFoundException($"Object not found in fake storage: {key}");
        }

        using var stream = new MemoryStream(bytes, writable: false);
        await consumer(stream, cancellationToken).ConfigureAwait(false);
    }

    public Task Delete(string key, string versionId, CancellationToken cancellationToken = default)
    {
        if (_versions.TryGetValue(key, out var currentVersion) && currentVersion == versionId)
        {
            _objects.TryRemove(key, out _);
            _versions.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<StorageObjectInfo> ListObjects(string? prefix = null, CancellationToken cancellationToken = default) =>
        ListObjects(prefix, null, cancellationToken);

    public async IAsyncEnumerable<StorageObjectInfo> ListObjects(
        string? prefix,
        StorageObjectCursor? cursor,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var key in _objects.Keys)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
            {
                yield return new StorageObjectInfo(key, DateTimeOffset.UtcNow, _objects[key].LongLength,
                    _versions.TryGetValue(key, out var versionId) ? versionId : null);
            }
        }

        await Task.CompletedTask;
    }

    public Task<StorageObjectPage> ListObjectVersionsPage(
        string? prefix,
        StorageObjectCursor? cursor,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);

        cancellationToken.ThrowIfCancellationRequested();
        StorageObjectInfo[] objects = _objects.Keys
            .Where(key => prefix is null || key.StartsWith(prefix, StringComparison.Ordinal))
            .Where(key => cursor is null || string.Compare(key, cursor.Key, StringComparison.Ordinal) > 0)
            .OrderBy(key => key, StringComparer.Ordinal)
            .Select(key => new StorageObjectInfo(key, DateTimeOffset.UtcNow, _objects[key].LongLength,
                _versions.TryGetValue(key, out var versionId) ? versionId : null))
            .Take(pageSize + 1)
            .ToArray();
        var complete = objects.Length <= pageSize;
        StorageObjectInfo[] page = objects.Take(pageSize).ToArray();
        StorageObjectCursor? next = complete || page.Length == 0
            ? null
            : new StorageObjectCursor(page[^1].Key, page[^1].VersionId);
        return Task.FromResult(new StorageObjectPage(page, next, complete));
    }

    /// <summary>Test helper: seeds a pre-encrypted object directly (bypassing Upload's stream copy).</summary>
    public void Seed(string key, byte[] content) => _objects[key] = content;

    public void SeedVersioned(string key, byte[] content, string versionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(versionId);
        _objects[key] = content;
        _versions[key] = versionId;
    }

    public bool Contains(string key) => _objects.ContainsKey(key);
}
