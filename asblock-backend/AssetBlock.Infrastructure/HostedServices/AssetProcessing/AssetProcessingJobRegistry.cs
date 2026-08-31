using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.Infrastructure.HostedServices.AssetProcessing;

/// <summary>
/// Thrown when a job handler returns an invalid, null, or type-mismatched result outcome.
/// Maps immediately to terminal INVALID_JOB_RESULT without retry.
/// </summary>
public sealed class InvalidAssetProcessingJobResultException(string message, Exception? innerException = null)
    : Exception(message, innerException);

public interface IAssetProcessingJobHandlerAdapter
{
    AssetProcessingJobType JobType { get; }
    Type PayloadType { get; }
    Type ResultType { get; }

    Task<AssetProcessingJobOutcome> Execute(
        IServiceProvider serviceProvider,
        ClaimedAssetProcessingJob claimedJob,
        CancellationToken cancellationToken);
}

public sealed class AssetProcessingJobHandlerAdapter<THandler, TPayload, TResult>(AssetProcessingJobType jobType)
    : IAssetProcessingJobHandlerAdapter
    where THandler : IAssetProcessingJobHandler<TPayload, TResult>
    where TPayload : AssetProcessingPayload
    where TResult : AssetProcessingResult
{
    public AssetProcessingJobType JobType { get; } = jobType;
    public Type PayloadType => typeof(TPayload);
    public Type ResultType => typeof(TResult);

    public async Task<AssetProcessingJobOutcome> Execute(
        IServiceProvider serviceProvider,
        ClaimedAssetProcessingJob claimedJob,
        CancellationToken cancellationToken)
    {
        THandler handler = serviceProvider.GetRequiredService<THandler>();

        AssetProcessingPayload rawPayload = AssetProcessingSerializer.DeserializePayload(JobType, claimedJob.Payload);
        if (rawPayload is not TPayload typedPayload)
        {
            throw new AssetProcessingSerializerException(
                $"Deserialized payload for {JobType} is not of expected type {typeof(TPayload).Name}.");
        }

        var context = new AssetProcessingJobContext<TPayload>(
            claimedJob.JobId,
            claimedJob.LeaseToken,
            claimedJob.AssetId,
            claimedJob.AssetVersionId,
            claimedJob.DefinitionVersion,
            claimedJob.AttemptCount,
            claimedJob.MaxAttempts,
            typedPayload,
            claimedJob.TraceParent,
            cancellationToken);

        AssetProcessingJobOutcome? outcome = await handler.Process(context, cancellationToken);

        if (outcome is null)
        {
            throw new InvalidAssetProcessingJobResultException(
                $"Handler for {JobType} returned a null outcome.");
        }

        if (outcome is AssetProcessingJobOutcome.Success success)
        {
            if (success.Result is null)
            {
                throw new InvalidAssetProcessingJobResultException(
                    $"Handler for {JobType} returned a null result in Success outcome.");
            }

            if (success.Result is not TResult)
            {
                throw new InvalidAssetProcessingJobResultException(
                    $"Handler for {JobType} returned result of type {success.Result.GetType().Name} instead of {typeof(TResult).Name}.");
            }
        }

        return outcome;
    }
}

public interface IAssetProcessingJobRegistry
{
    IAssetProcessingJobHandlerAdapter? GetHandler(AssetProcessingJobType type);
    bool HasHandler(AssetProcessingJobType type);
}

public sealed class AssetProcessingJobRegistry : IAssetProcessingJobRegistry
{
    private readonly IReadOnlyDictionary<AssetProcessingJobType, IAssetProcessingJobHandlerAdapter> _adapters;

    public AssetProcessingJobRegistry(IEnumerable<IAssetProcessingJobHandlerAdapter> adapters)
    {
        var list = adapters.ToList();
        var duplicates = list.GroupBy(a => a.JobType).Where(g => g.Count() > 1).ToList();
        if (duplicates.Count > 0)
        {
            throw new InvalidOperationException(
                $"Duplicate job handlers registered for types: {string.Join(", ", duplicates.Select(d => d.Key))}.");
        }

        _adapters = list.ToDictionary(a => a.JobType);
    }

    public IAssetProcessingJobHandlerAdapter? GetHandler(AssetProcessingJobType type) =>
        _adapters.GetValueOrDefault(type);

    public bool HasHandler(AssetProcessingJobType type) =>
        _adapters.ContainsKey(type);
}
