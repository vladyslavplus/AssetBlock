using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Assets.GetAssetVersions;

public sealed record GetAssetVersionsQuery(
    Guid AssetId,
    Guid? RequesterUserId) : IRequest<Result<IReadOnlyList<AssetVersionSummaryDto>>>;
