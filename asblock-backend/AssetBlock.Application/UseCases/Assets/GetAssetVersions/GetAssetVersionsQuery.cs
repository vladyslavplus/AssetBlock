using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetAssetVersions;

public sealed record GetAssetVersionsQuery(
    Guid AssetId,
    Guid? RequesterUserId) : IRequest<Result<IReadOnlyList<AssetVersionSummaryDto>>>;
