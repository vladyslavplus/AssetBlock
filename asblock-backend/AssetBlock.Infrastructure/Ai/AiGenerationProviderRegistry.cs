using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Enums;
using System.Diagnostics.CodeAnalysis;

namespace AssetBlock.Infrastructure.Ai;

internal sealed class AiGenerationProviderRegistry : IAiGenerationProviderRegistry
{
    private readonly Dictionary<AiProviderKind, IAiGenerationProvider> _providers = new();

    public AiGenerationProviderRegistry(IEnumerable<IAiGenerationProvider> providers)
    {
        foreach (var provider in providers)
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
