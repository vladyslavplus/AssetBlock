using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAiGenerationProvider
{
    AiProviderKind Kind { get; }
    int MaxInputChars { get; }
    int MaxOutputTokens { get; }
    IReadOnlyList<string> OrderedModelIds { get; }

    Task<AiGenerationProviderResult> Generate(
        AiGenerationRequest request,
        CancellationToken cancellationToken);
}
