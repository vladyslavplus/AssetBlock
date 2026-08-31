using System.Diagnostics.CodeAnalysis;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Infrastructure.Ai;

internal sealed class AiGenerationProviderRegistry : IAiGenerationProviderRegistry
{
    private readonly Dictionary<AiProviderKind, IAiGenerationProvider> _providers = new();

    public AiGenerationProviderRegistry(IEnumerable<IAiGenerationProvider> providers)
    {
        foreach (IAiGenerationProvider provider in providers)
        {
            if (!_providers.TryAdd(provider.Kind, provider))
            {
                throw new InvalidOperationException($"Duplicate AI generation provider for {provider.Kind}.");
            }
        }
    }

    public bool TryGet(AiProviderKind kind, [NotNullWhen(true)] out IAiGenerationProvider? provider) =>
        _providers.TryGetValue(kind, out provider);
}
