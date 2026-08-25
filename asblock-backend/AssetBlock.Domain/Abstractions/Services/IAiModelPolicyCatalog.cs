using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using System.Diagnostics.CodeAnalysis;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAiModelPolicyCatalog
{
    int SchemaVersion { get; }

    bool TryGet(
        AiProviderKind provider,
        string modelId,
        [NotNullWhen(true)] out AiModelPolicyEntry? entry);
}
