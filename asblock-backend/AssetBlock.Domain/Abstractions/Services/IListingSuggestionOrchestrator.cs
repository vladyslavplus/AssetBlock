using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IListingSuggestionOrchestrator
{
    Task<ListingSuggestionResult> Generate(
        ListingSuggestionGenerationRequest request,
        CancellationToken cancellationToken);
}
