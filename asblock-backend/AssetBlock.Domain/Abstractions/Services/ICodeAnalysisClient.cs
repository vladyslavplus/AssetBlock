using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Authenticated, bounded client for the external analysis service.</summary>
public interface ICodeAnalysisClient
{
    Task<CodeAnalysisServiceResponse> Send(
        CodeAnalysisMetadataRequest metadata,
        Func<Stream, CancellationToken, Task> writeArchive,
        CancellationToken cancellationToken);
}
