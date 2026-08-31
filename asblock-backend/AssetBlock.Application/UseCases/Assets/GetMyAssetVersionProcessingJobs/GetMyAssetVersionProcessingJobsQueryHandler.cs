using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Application.UseCases.Assets.GetMyAssetVersionProcessingJobs;

internal sealed class GetMyAssetVersionProcessingJobsQueryHandler(
    IAssetProcessingJobStore jobStore
) : IRequestHandler<GetMyAssetVersionProcessingJobsQuery, Result<IReadOnlyList<AssetProcessingJobDto>>>
{
    public async Task<Result<IReadOnlyList<AssetProcessingJobDto>>> Handle(
        GetMyAssetVersionProcessingJobsQuery request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<AssetProcessingJobDto>? jobs = await jobStore.GetJobsForVersion(request.AssetVersionId, request.OwnerUserId, cancellationToken);
        if (jobs is null)
        {
            return Result.NotFound("Asset version was not found or is inaccessible.");
        }

        return Result.Success(jobs);
    }
}
