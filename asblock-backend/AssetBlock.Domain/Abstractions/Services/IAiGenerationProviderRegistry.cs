using AssetBlock.Domain.Core.Enums;
using System.Diagnostics.CodeAnalysis;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAiGenerationProviderRegistry
{
    bool TryGet(AiProviderKind kind, [NotNullWhen(true)] out IAiGenerationProvider? provider);
}
