using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Application.UseCases.Assets.GetMyAssetProcessingJobs;

public sealed record GetMyAssetProcessingJobsQuery(
    Guid AssetId,
    Guid OwnerUserId
) : IRequest<Result<IReadOnlyList<AssetProcessingJobDto>>>;
