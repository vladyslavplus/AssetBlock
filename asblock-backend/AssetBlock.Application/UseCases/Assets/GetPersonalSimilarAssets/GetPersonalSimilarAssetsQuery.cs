using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;

namespace AssetBlock.Application.UseCases.Assets.GetPersonalSimilarAssets;

public sealed record GetPersonalSimilarAssetsQuery(
    Guid AssetId,
    Guid UserId,
    int Limit = SimilarAssetsConstants.DEFAULT_LIMIT,
    string Mode = SimilarAssetsConstants.MODE_SIMILARITY) : IRequest<Result<SimilarAssetsResult>>;
