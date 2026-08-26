using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Application.UseCases.Assets.GetMyAssetVersionProcessingJobs;

public sealed record GetMyAssetVersionProcessingJobsQuery(
    Guid AssetVersionId,
    Guid OwnerUserId
) : IRequest<Result<IReadOnlyList<AssetProcessingJobDto>>>;
