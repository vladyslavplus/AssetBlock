using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IArchiveSafetyInspector
{
    Task<ArchiveSafetyResult> Inspect(
        Stream archiveStream,
        string fileName,
        CancellationToken cancellationToken = default);
}
