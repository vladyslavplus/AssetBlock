namespace AssetBlock.Domain.Abstractions.Services;

public interface IAssetStorageService
{
    Task EnsureBucketAsync(CancellationToken cancellationToken = default);
    Task Upload(string key, Stream content, CancellationToken cancellationToken = default);
    Task<Stream> Get(string key, CancellationToken cancellationToken = default);
}
