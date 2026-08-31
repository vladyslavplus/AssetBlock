using System.Diagnostics.CodeAnalysis;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAiGenerationProviderRegistry
{
    bool TryGet(AiProviderKind kind, [NotNullWhen(true)] out IAiGenerationProvider? provider);
}
