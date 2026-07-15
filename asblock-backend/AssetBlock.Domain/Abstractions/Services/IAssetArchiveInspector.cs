namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Inspects an archive stream for policy violations before encryption/storage.
/// Throws <see cref="Core.Exceptions.ArchiveRejectedException"/> when the archive is rejected.
/// </summary>
public interface IAssetArchiveInspector
{
    Task Inspect(Stream archiveStream, string fileName, CancellationToken cancellationToken = default);
}
