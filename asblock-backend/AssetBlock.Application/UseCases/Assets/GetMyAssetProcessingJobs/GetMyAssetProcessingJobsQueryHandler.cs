using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Application.UseCases.Assets.GetMyAssetProcessingJobs;

internal sealed class GetMyAssetProcessingJobsQueryHandler(
    IAssetProcessingJobStore jobStore
) : IRequestHandler<GetMyAssetProcessingJobsQuery, Result<IReadOnlyList<AssetProcessingJobDto>>>
{
    public async Task<Result<IReadOnlyList<AssetProcessingJobDto>>> Handle(
        GetMyAssetProcessingJobsQuery request,
        CancellationToken cancellationToken)
    {
        var jobs = await jobStore.GetJobsForAsset(request.AssetId, request.OwnerUserId, cancellationToken);
        if (jobs is null)
        {
            return Result.NotFound("Asset was not found or is inaccessible.");
        }

        return Result.Success(jobs);
    }
}
