using AssetBlock.Domain.Abstractions.Services;

namespace AssetBlock.SearchEvaluation.Infrastructure;

/// <summary>
/// Capability mock for search evaluation benchmark runner.
/// </summary>
public sealed class BenchmarkVectorSearchCapability(string modelKey, bool isAvailable = true) : IVectorSearchCapability
{
    public Task<VectorSearchCapabilityResult> CheckCapability(CancellationToken cancellationToken = default) =>
        Task.FromResult(isAvailable
            ? VectorSearchCapabilityResult.Available(modelKey)
            : VectorSearchCapabilityResult.Disabled());

    public Task<bool> IsVectorSearchAvailable(CancellationToken cancellationToken = default) =>
        Task.FromResult(isAvailable);
}
