using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetSimilarAssets;

public sealed record GetSimilarAssetsQuery(
    Guid AssetId,
    int Limit = SimilarAssetsConstants.DEFAULT_LIMIT,
    string Mode = SimilarAssetsConstants.MODE_SIMILARITY) : IRequest<Result<SimilarAssetsResult>>;
